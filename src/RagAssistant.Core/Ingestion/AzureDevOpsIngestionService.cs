using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RagAssistant.Core.Ingestion;

/// <summary>
/// Document source for an Azure DevOps (Server) git repository: lists .md files
/// under the configured path via the items API and streams their contents.
/// Mirroring to disk and ingestion are handled by <see cref="DocumentSourceSynchronizer"/>.
/// </summary>
public sealed class AzureDevOpsIngestionService(
    IHttpClientFactory httpFactory,
    DocumentSourceSynchronizer synchronizer,
    IOptions<AzureDevOpsOptions> adoOptions,
    ILogger<AzureDevOpsIngestionService> logger) : IDocumentSource
{
    private readonly AzureDevOpsOptions _ado = adoOptions.Value;

    // Cache folder becomes "{DocsFolder}/ado-sync" — matches the compose volume mount.
    public string Name => "ado";

    public bool IsConfigured => !string.IsNullOrEmpty(_ado.BaseUrl);

    /// <summary>
    /// Downloads all .md files from the configured ADO repo path into the local cache
    /// directory, then runs the shared ingestion pipeline.
    /// Safe to call concurrently — a second call while one is in progress is a no-op.
    /// </summary>
    public Task SyncAndIngestAsync(CancellationToken ct = default) =>
        synchronizer.SyncAndIngestAsync(this, ct);

    public async IAsyncEnumerable<RawDocument> SyncAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = CreateHttpClient();

        var remoteFiles = await ListMarkdownFilesAsync(http, ct);
        logger.LogInformation("ADO: {Count} markdown file(s) in {Repo}{Path}",
            remoteFiles.Count, _ado.Repository, _ado.Path);

        foreach (var remotePath in remoteFiles)
        {
            var url = ItemsBaseUrl()
                + $"?path={Uri.EscapeDataString(remotePath)}"
                + $"&versionDescriptor.versionType=branch&versionDescriptor.version={Uri.EscapeDataString(_ado.Branch)}"
                + "&api-version=6.0";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

            var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);

            yield return new RawDocument(remotePath.TrimStart('/'), content);
        }
    }

    /// <summary>
    /// Validates the X-Hub-Signature HMAC-SHA1 header that ADO Server attaches when a
    /// WebhookSecret is configured on the subscription. Returns true if no secret is
    /// configured (open webhook) or if the signature matches.
    /// SHA-1 is the only algorithm ADO Server supports for webhook signatures. HMAC-SHA1
    /// is not affected by SHA-1 collision attacks (they require chosen prefixes, which
    /// HMAC's keyed construction prevents), so this is acceptable — but it cannot be
    /// upgraded until ADO does.
    /// </summary>
    public bool ValidateWebhookSignature(string payload, string signatureHeader)
    {
        if (string.IsNullOrEmpty(_ado.WebhookSecret))
            return true;

        if (!signatureHeader.StartsWith("sha1=", StringComparison.OrdinalIgnoreCase))
            return false;

        var expected = signatureHeader[5..].ToLowerInvariant();
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_ado.WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(expected));
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private async Task<List<string>> ListMarkdownFilesAsync(HttpClient http, CancellationToken ct)
    {
        var url = ItemsBaseUrl()
            + $"?path={Uri.EscapeDataString(_ado.Path)}&recursionLevel=Full"
            + $"&versionDescriptor.versionType=branch&versionDescriptor.version={Uri.EscapeDataString(_ado.Branch)}"
            + "&api-version=6.0";

        var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return doc.RootElement
            .GetProperty("value")
            .EnumerateArray()
            .Where(e => !(e.TryGetProperty("isFolder", out var f) && f.GetBoolean()))
            .Select(e => e.GetProperty("path").GetString() ?? "")
            .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private HttpClient CreateHttpClient()
    {
        var http = httpFactory.CreateClient("ado");
        if (!string.IsNullOrEmpty(_ado.PersonalAccessToken))
        {
            var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_ado.PersonalAccessToken}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }
        return http;
    }

    private string ItemsBaseUrl() =>
        $"{_ado.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(_ado.Project)}/_apis/git/repositories/{Uri.EscapeDataString(_ado.Repository)}/items";
}
