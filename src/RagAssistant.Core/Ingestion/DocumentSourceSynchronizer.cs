using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RagAssistant.Core.Ingestion;

/// <summary>
/// Mirrors an <see cref="IDocumentSource"/> into a cache folder under the docs
/// folder ("{DocsFolder}/{source.Name}-sync"), deletes files that disappeared
/// from the source, then runs the shared ingestion pipeline. A per-instance lock
/// makes concurrent webhook-triggered syncs a no-op instead of a pile-up.
/// </summary>
public sealed class DocumentSourceSynchronizer(
    MarkdownIngestionService ingestion,
    IOptions<RagOptions> ragOptions,
    ILogger<DocumentSourceSynchronizer> logger)
{
    private readonly RagOptions _rag = ragOptions.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SyncAndIngestAsync(IDocumentSource source, CancellationToken ct = default)
    {
        if (!await _lock.WaitAsync(0, ct))
        {
            logger.LogInformation("{Source} sync already in progress, skipping.", source.Name);
            return;
        }

        try
        {
            var cacheDir = Path.Combine(Path.GetFullPath(_rag.DocsFolder), $"{source.Name}-sync");
            Directory.CreateDirectory(cacheDir);

            logger.LogInformation("Starting {Source} sync into {Dir}", source.Name, cacheDir);

            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;

            await foreach (var doc in source.SyncAsync(ct))
            {
                var relative = doc.RelativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var localPath = Path.Combine(cacheDir, relative);
                var dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(localPath, doc.Content, ct);
                kept.Add(localPath);
                count++;
            }

            foreach (var existing in Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories))
            {
                if (kept.Contains(existing)) continue;
                File.Delete(existing);
                logger.LogInformation("Removed stale local file: {Path}", existing);
            }

            logger.LogInformation("Synced {Count} file(s) from {Source}, running ingestion.", count, source.Name);
            await ingestion.IngestAllAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
