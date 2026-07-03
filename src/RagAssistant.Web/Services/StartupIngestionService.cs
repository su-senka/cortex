using RagAssistant.Core.Ingestion;

namespace RagAssistant.Web.Services;

/// <summary>
/// Runs the initial document ingestion in the background so the app serves traffic
/// immediately after startup instead of blocking until embedding completes.
/// Progress is reported through <see cref="IngestionStatusService"/> and surfaced
/// on /health/ready and /api/ingest/status.
/// </summary>
public sealed class StartupIngestionService(
    MarkdownIngestionService ingestion,
    AzureDevOpsIngestionService adoIngestion,
    IngestionStatusService status,
    IConfiguration configuration,
    ILogger<StartupIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before we begin hammering Ollama.
        await Task.Yield();

        status.MarkRunning();

        var adoBaseUrl = configuration["AzureDevOps:BaseUrl"];

        try
        {
            if (!string.IsNullOrEmpty(adoBaseUrl))
            {
                try
                {
                    logger.LogInformation("Running startup ADO sync...");
                    await adoIngestion.SyncAndIngestAsync(stoppingToken);
                    logger.LogInformation("Startup ADO sync complete.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Startup ADO sync failed — falling back to local ingestion.");
                    await ingestion.IngestAllAsync(stoppingToken);
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
