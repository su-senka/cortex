using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using NSubstitute;
using RagAssistant.Core.Models;

namespace RagAssistant.Tests.Support;

/// <summary>
/// Per-test temp directory for SQLite files and doc folders. Clears the SQLite
/// connection pool on dispose so the files can actually be deleted.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cortex-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

internal static class Stubs
{
    /// <summary>
    /// Embedding generator that returns a fixed small vector per input.
    /// <paramref name="capture"/> receives every batch of input texts.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> Embedder(
        Action<IReadOnlyList<string>>? capture = null)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ci.Arg<IEnumerable<string>>().ToList();
                capture?.Invoke(inputs);
                return new GeneratedEmbeddings<Embedding<float>>(
                    inputs.Select(_ => new Embedding<float>(new float[] { 1f, 0f, 0f })));
            });
        return embedder;
    }

    public static DocumentChunk Chunk(
        string key,
        string content = "Some content.",
        string tags = "",
        string title = "Doc",
        string? sourceFile = null,
        string sectionHeading = "") => new()
    {
        Key = key,
        Content = content,
        Tags = tags,
        Title = title,
        // Real keys are "{sourceFile}#{index}" — default to that convention.
        SourceFile = sourceFile ?? key.Split('#')[0],
        SectionHeading = sectionHeading,
    };

    public static async IAsyncEnumerable<VectorSearchResult<DocumentChunk>> SearchResults(
        params DocumentChunk[] chunks)
    {
        foreach (var (chunk, i) in chunks.Select((c, i) => (c, i)))
            yield return new VectorSearchResult<DocumentChunk>(chunk, 0.9 - i * 0.1);
        await Task.CompletedTask;
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> StreamedText(params string[] tokens)
    {
        foreach (var token in tokens)
            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
        await Task.CompletedTask;
    }
}
