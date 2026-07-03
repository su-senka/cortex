using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RagAssistant.Core;

namespace RagAssistant.Web.Health;

/// <summary>
/// Readiness check: verifies the Ollama API is reachable. Uses /api/tags because it
/// is cheap and requires no model to be loaded.
/// </summary>
public sealed class OllamaHealthCheck(
    IHttpClientFactory httpFactory,
    IOptions<OllamaOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var http = httpFactory.CreateClient("health");
            var url  = new Uri(new Uri(options.Value.BaseUrl), "/api/tags");
            using var response = await http.GetAsync(url, cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Ollama reachable.")
                : HealthCheckResult.Unhealthy($"Ollama returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Ollama unreachable.", ex);
        }
    }
}
