using RagAssistant.Core.Ingestion;

namespace RagAssistant.Web.Services;

/// <summary>
/// Runs the initial document ingestion in the background so the app serves traffic
/// immediately after startup instead of blocking until embedding completes.
/// Configured remote sources (ADO, GitHub) are synced first; if none are configured
/// the local docs folder is ingested directly.
/// Progress is reported through <see cref="IngestionStatusService"/> and surfaced
/// on /health/ready and /api/ingest/status.
/// </summary>
public sealed class StartupIngestionService(
    MarkdownIngestionService ingestion,
    DocumentSourceSynchronizer synchronizer,
    AzureDevOpsIngestionService adoSource,
    GitHubIngestionService gitHubSource,
    IngestionStatusService status,
    ILogger<StartupIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before we begin hammering Ollama.
        await Task.Yield();

        status.MarkRunning();

        IDocumentSource[] sources = [adoSource, gitHubSource];
        var configured = sources.Where(s => s.IsConfigured).ToList();

        try
        {
            if (configured.Count > 0)
            {
                foreach (var source in configured)
                {
                    try
                    {
                        logger.LogInformation("Running startup {Source} sync...", source.Name);
                        await synchronizer.SyncAndIngestAsync(source, stoppingToken);
                        logger.LogInformation("Startup {Source} sync complete.", source.Name);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Startup {Source} sync failed — falling back to local ingestion.", source.Name);
                        await ingestion.IngestAllAsync(stoppingToken);
                    }
                }
            }
            else
            {
                logger.LogInformation("Running startup ingestion...");
                await ingestion.IngestAllAsync(stoppingToken);
                logger.LogInformation("Startup ingestion complete.");
            }

            status.MarkCompleted();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is shutting down — leave the status as-is.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup ingestion failed — app keeps serving the existing index.");
            status.MarkFailed(ex.Message);
        }
    }
}
