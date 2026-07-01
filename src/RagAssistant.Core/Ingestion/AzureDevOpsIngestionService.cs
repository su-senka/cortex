using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RagAssistant.Core.Ingestion;

public sealed class AzureDevOpsIngestionService(
    IHttpClientFactory httpFactory,
    MarkdownIngestionService ingestion,
    IOptions<AzureDevOpsOptions> adoOptions,
    IOptions<RagOptions> ragOptions,
    ILogger<AzureDevOpsIngestionService> logger)
{
    private readonly AzureDevOpsOptions _ado = adoOptions.Value;
    private readonly RagOptions _rag = ragOptions.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Downloads all .md files from the configured ADO repo path into a local cache
    /// directory, then delegates to MarkdownIngestionService.IngestAllAsync so the
    /// existing embedding pipeline runs unchanged.
    /// Safe to call concurrently — a second call while one is in progress is a no-op.
    /// </summary>
    public async Task SyncAndIngestAsync(CancellationToken ct = default)
    {
        if (!await _lock.WaitAsync(0, ct))
        {
            logger.LogInformation("ADO sync already in progress, skipping.");
            return;
        }

        try
        {
            logger.LogInformation("Starting ADO sync from {Repo}/{Path}", _ado.Repository, _ado.Path);

            using var http = CreateHttpClient();
            var cacheDir = GetCacheDirectory();
            Directory.CreateDirectory(cacheDir);

            var remoteFiles = await ListMarkdownFilesAsync(http, ct);
            var downloadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var remotePath in remoteFiles)
            {
                var localPath = RemotePathToLocal(cacheDir, remotePath);
                await DownloadFileAsync(http, remotePath, localPath, ct);
                downloadedPaths.Add(localPath);
            }

            // Remove files that no longer exist in the ADO repo.
            foreach (var existing in Directory.GetFiles(cacheDir, "*.md", SearchOption.AllDirectories))
            {
                if (downloadedPaths.Contains(existing)) continue;
                
                File.Delete(existing);
                logger.LogInformation("Removed stale local file: {Path}", existing);
            }

            logger.LogInformation("Downloaded {Count} file(s) from ADO, running ingestion.", remoteFiles.Count);
            await ingestion.IngestAllAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Validates the X-Hub-Signature HMAC-SHA1 header that ADO Server attaches when a
    /// WebhookSecret is configured on the subscription. Returns true if no secret is
    /// configured (open webhook) or if the signature matches.
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

    private async Task DownloadFileAsync(
        HttpClient http, string remotePath, string localPath, CancellationToken ct)
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
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(localPath, content, ct);
        logger.LogDebug("Downloaded {Remote} → {Local}", remotePath, localPath);
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

    private string GetCacheDirectory() =>
        Path.Combine(Path.GetFullPath(_rag.DocsFolder), "ado-sync");

    private static string RemotePathToLocal(string cacheDir, string remotePath)
    {
        var relative = remotePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(cacheDir, relative);
    }
}
