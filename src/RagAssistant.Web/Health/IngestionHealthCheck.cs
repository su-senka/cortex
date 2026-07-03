using Microsoft.Extensions.Diagnostics.HealthChecks;
using RagAssistant.Web.Services;

namespace RagAssistant.Web.Health;

/// <summary>
/// Reports background ingestion state on the readiness endpoint. A failed or
/// still-running ingestion is Degraded, not Unhealthy — the app can still answer
/// questions from the existing index, so it must keep receiving traffic.
/// </summary>
public sealed class IngestionHealthCheck(IngestionStatusService status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = status.Snapshot();

        var result = snapshot.State switch
        {
            "Completed" => HealthCheckResult.Healthy("Ingestion complete."),
            "Failed"    => HealthCheckResult.Degraded($"Ingestion failed: {snapshot.Error}"),
            _           => HealthCheckResult.Degraded($"Ingestion {snapshot.State.ToLowerInvariant()}."),
        };

        return Task.FromResult(result);
    }
}
