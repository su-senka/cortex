using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagAssistant.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RagAssistant.Core.Ingestion;

public sealed class MarkdownIngestionService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly VectorStoreCollection<string, DocumentChunk> _collection;
    private readonly RagOptions _options;
    private readonly ILogger<MarkdownIngestionService> _logger;

    // Matches any ATX heading line: optional leading spaces, 1–6 #, required space, text.
    private static readonly Regex HeadingRegex =
        new(@"^[ \t]*(#{1,6})[ \t]+(.+?)[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public MarkdownIngestionService(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        VectorStoreCollection<string, DocumentChunk> collection,
        IOptions<RagOptions> options,
        ILogger<MarkdownIngestionService> logger)
    {
        _embedder = embedder;
        _collection = collection;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Scans the configured docs folder, embeds any new or changed content,
    /// and removes chunks from files that have since been deleted.
    /// Safe to call on every startup — deterministic keys make it fully idempotent.
    /// </summary>
    public async Task IngestAllAsync(CancellationToken ct = default)
    {
        await _collection.EnsureCollectionExistsAsync(ct);

        var docsFolder = Path.GetFullPath(_options.DocsFolder);
        if (!Directory.Exists(docsFolder))
        {
            _logger.LogWarning("Docs folder not found: {Path}", docsFolder);
            return;
        }

        // Metadata file tracks how many chunks each source file produced last time.
        // We need this to delete "tail" chunks when a file shrinks.
        var metadataPath = DeriveMetadataPath(_options.VectorDbPath);
        var metadata = await LoadMetadataAsync(metadataPath, ct);

        var mdFiles = Directory.GetFiles(docsFolder, "*.md", SearchOption.AllDirectories);
        var currentRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var absolutePath in mdFiles)
        {
            var relativePath = Path.GetRelativePath(docsFolder, absolutePath).Replace('\\', '/');
            currentRelativePaths.Add(relativePath);

            try
            {
                await IngestFileAsync(absolutePath, relativePath, metadata, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ingest {File}", relativePath);
            }
        }

        // Remove chunks from files no longer on disk.
        foreach (var (path, oldChunkCount) in metadata.ToList())
        {
            if (currentRelativePaths.Contains(path)) continue;

            _logger.LogInformation("Removing deleted file from store: {Path}", path);
            var staleKeys = Enumerable.Range(0, oldChunkCount).Select(i => BuildKey(path, i));
            await _collection.DeleteAsync(staleKeys, ct);
            metadata.Remove(path);
        }

        await SaveMetadataAsync(metadataPath, metadata, ct);
        _logger.LogInformation("Ingestion complete — {Count} file(s) processed.", mdFiles.Length);
    }

    private async Task IngestFileAsync(
        string absolutePath,
        string relativePath,
        Dictionary<string, int> metadata,
        CancellationToken ct)
    {
        var rawText = await File.ReadAllTextAsync(absolutePath, ct);
        var (frontMatter, body) = ParseFrontMatter(rawText);
        var chunks = BuildChunks(body, frontMatter, relativePath);

        _logger.LogDebug("Ingesting {File}: {N} chunk(s)", relativePath, chunks.Count);

        if (chunks.Count == 0)
        {
            _logger.LogWarning("No content found in {File}, skipping.", relativePath);
            return;
        }

        // Embed all chunk texts in one batch to avoid N individual round-trips to Ollama.
        var texts = chunks.Select(c => c.Content).ToList();
        var embeddings = await _embedder.GenerateAsync(texts, cancellationToken: ct);
        for (int i = 0; i < chunks.Count; i++)
            chunks[i].Embedding = embeddings[i].Vector;

        await _collection.UpsertAsync(chunks, ct);

        // Delete trailing stale chunks if this ingest produced fewer chunks than last time.
        if (metadata.TryGetValue(relativePath, out var oldCount) && oldCount > chunks.Count)
        {
            var staleKeys = Enumerable.Range(chunks.Count, oldCount - chunks.Count)
                .Select(i => BuildKey(relativePath, i));
            await _collection.DeleteAsync(staleKeys, ct);
            _logger.LogDebug("Deleted {N} stale chunk(s) from {File}", oldCount - chunks.Count, relativePath);
        }

        metadata[relativePath] = chunks.Count;
    }

    // ── Chunking ──────────────────────────────────────────────────────────────────

    private List<DocumentChunk> BuildChunks(string body, FrontMatterData fm, string relativePath)
    {
        var chunks = new List<DocumentChunk>();
        string? previousTail = null; // last ChunkOverlap chars of the preceding chunk

        foreach (var (headingPath, sectionText) in SplitIntoSections(body))
        {
            var paragraphBlocks = SplitByParagraph(sectionText, _options.ChunkSize);

            foreach (var block in paragraphBlocks)
            {
                // Section heading prefix gives the retriever context even when the chunk
                // is surfaced without surrounding document text.
                var headingPrefix = string.IsNullOrEmpty(headingPath)
                    ? string.Empty
                    : $"[Section: {headingPath}]\n\n";

                // Overlap appended after the prefix keeps sentence continuity across boundaries.
                var content = headingPrefix + (previousTail ?? string.Empty) + block;

                chunks.Add(new DocumentChunk
                {
                    Key = BuildKey(relativePath, chunks.Count),
                    Content = content,
                    SourceFile = relativePath,
                    Title = fm.Title ?? Path.GetFileNameWithoutExtension(relativePath),
                    Tags = string.Join(", ", fm.Tags ?? []),
                    LastVerified = fm.LastVerified ?? string.Empty,
                    SectionHeading = headingPath,
                    ChunkIndex = chunks.Count,
                });

                previousTail = block.Length > _options.ChunkOverlap
                    ? block[^_options.ChunkOverlap..]
                    : block;
            }
        }

        return chunks;
    }

    /// <summary>
    /// Splits markdown into (headingBreadcrumb, bodyText) pairs at ATX heading boundaries.
    /// Text before the first heading is yielded under an empty breadcrumb.
    /// </summary>
    private static IEnumerable<(string Heading, string Text)> SplitIntoSections(string markdown)
    {
        var matches = HeadingRegex.Matches(markdown);
        var headingStack = new SortedDictionary<int, string>(); // level → text

        var preamble = matches.Count > 0
            ? markdown[..matches[0].Index].Trim()
            : markdown.Trim();

        if (!string.IsNullOrWhiteSpace(preamble))
            yield return (string.Empty, preamble);

        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            int level = m.Groups[1].Length;
            var headingText = m.Groups[2].Value.Trim();

            headingStack[level] = headingText;
            foreach (var k in headingStack.Keys.Where(k => k > level).ToList())
                headingStack.Remove(k);

            var breadcrumb = string.Join(" > ", headingStack.Values);

            var textStart = m.Index + m.Length;
            var textEnd = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var body = markdown[textStart..textEnd].Trim();

            if (!string.IsNullOrWhiteSpace(body))
                yield return (breadcrumb, body);
        }
    }

    /// <summary>
    /// Splits text into paragraph-aligned blocks ≤ <paramref name="maxSize"/> chars.
    /// A single paragraph that exceeds maxSize is hard-split at the character level.
    /// </summary>
    private static List<string> SplitByParagraph(string text, int maxSize)
    {
        if (text.Length <= maxSize)
            return [text];

        var paragraphs = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        var current = new StringBuilder();

        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (trimmed.Length == 0) continue;

            if (current.Length > 0 && current.Length + 2 + trimmed.Length > maxSize)
            {
                result.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0) current.Append("\n\n");
            current.Append(trimmed);

            if (current.Length > maxSize)
            {
                var big = current.ToString();
                for (int offset = 0; offset < big.Length; offset += maxSize)
                    result.Add(big[offset..Math.Min(offset + maxSize, big.Length)]);
                current.Clear();
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result.Count > 0 ? result : [text];
    }

    // ── Front matter ──────────────────────────────────────────────────────────────

    private static (FrontMatterData FrontMatter, string Body) ParseFrontMatter(string text)
    {
        if (!text.TrimStart().StartsWith("---"))
            return (new FrontMatterData(), text);

        var firstDelim = text.IndexOf("---", StringComparison.Ordinal);
        var secondDelim = text.IndexOf("---", firstDelim + 3, StringComparison.Ordinal);
        if (secondDelim == -1)
            return (new FrontMatterData(), text);

        var yaml = text[(firstDelim + 3)..secondDelim].Trim();
        var body = text[(secondDelim + 3)..].TrimStart();

        try
        {
            // UnderscoredNamingConvention maps YAML "last_verified" → C# "LastVerified".
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return (deserializer.Deserialize<FrontMatterData>(yaml) ?? new FrontMatterData(), body);
        }
        catch
        {
            return (new FrontMatterData(), body);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    // Key: "relative/path/to/file.md#0", "relative/path/to/file.md#1", ...
    // Deterministic so repeated ingestion of the same file produces the same keys.
    private static string BuildKey(string relativePath, int chunkIndex) =>
        $"{relativePath}#{chunkIndex}";

    private static string DeriveMetadataPath(string vectorDbPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(vectorDbPath)) ?? ".",
            "rag_metadata.json");

    private static async Task<Dictionary<string, int>> LoadMetadataAsync(
        string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [];
        }
        catch { return []; }
    }

    private static async Task SaveMetadataAsync(
        string path, Dictionary<string, int> metadata, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }
}

// Maps to YAML front matter fields. Internal so it's not exposed in public API.
internal sealed class FrontMatterData
{
    public string? Title { get; set; }
    public List<string>? Tags { get; set; }
    public string? Owner { get; set; }
    public string? LastVerified { get; set; }  // YAML: last_verified (UnderscoredNaming)
}
