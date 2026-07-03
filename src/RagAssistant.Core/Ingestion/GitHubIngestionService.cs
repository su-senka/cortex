using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RagAssistant.Core.Ingestion;

/// <summary>
/// Document source for a GitHub / GitHub Enterprise repository: lists the branch
/// tree via the git trees API and streams every .md file under the configured path.
/// Push webhooks are validated with the HMAC-SHA256 X-Hub-Signature-256 header.
/// </summary>
public sealed class GitHubIngestionService(
    IHttpClientFactory httpFactory,
    IOptions<GitHubOptions> options,
    ILogger<GitHubIngestionService> logger) : IDocumentSource
{
    private readonly GitHubOptions _gh = options.Value;

    public string Name => "github";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_gh.Owner) && !string.IsNullOrEmpty(_gh.Repository);

    public async IAsyncEnumerable<RawDocument> SyncAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = CreateHttpClient();

        var prefix = _gh.Path.Trim('/');
        var treeUrl = $"{RepoBaseUrl()}/git/trees/{Uri.EscapeDataString(_gh.Branch)}?recursive=1";

        var response = await http.GetAsync(treeUrl, ct);
        response.EnsureSuccessStatusCode();

        List<string> paths;
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)))
        {
            if (doc.RootElement.TryGetProperty("truncated", out var tr) && tr.GetBoolean())
                logger.LogWarning("GitHub tree listing truncated — repository too large for one page.");

            paths = doc.RootElement
                .GetProperty("tree")
                .EnumerateArray()
                .Where(e => e.GetProperty("type").GetString() == "blob")
                .Select(e => e.GetProperty("path").GetString() ?? "")
                .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Where(p => prefix.Length == 0 || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        logger.LogInformation("GitHub: {Count} markdown file(s) in {Owner}/{Repo}@{Branch}",
            paths.Count, _gh.Owner, _gh.Repository, _gh.Branch);

        foreach (var path in paths)
        {
            var contentUrl = $"{RepoBaseUrl()}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}"
                           + $"?ref={Uri.EscapeDataString(_gh.Branch)}";

            var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
            // raw media type returns the file body directly instead of base64 JSON.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

            var fileResponse = await http.SendAsync(request, ct);
            fileResponse.EnsureSuccessStatusCode();
            var content = await fileResponse.Content.ReadAsStringAsync(ct);

            var relative = prefix.Length == 0 ? path : path[(prefix.Length + 1)..];
            yield return new RawDocument(relative, content);
        }
    }

    /// <summary>
    /// Validates the X-Hub-Signature-256 header ("sha256=&lt;hex&gt;"). Returns true
    /// if no secret is configured (open webhook) or if the signature matches.
    /// </summary>
    public bool ValidateWebhookSignature(string payload, string signatureHeader)
    {
        if (string.IsNullOrEmpty(_gh.WebhookSecret))
            return true;

        if (!signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;

        var expected = signatureHeader[7..].ToLowerInvariant();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_gh.WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(expected));
    }

    private HttpClient CreateHttpClient()
    {
        var http = httpFactory.CreateClient("github");
        // GitHub rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cortex-rag");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrEmpty(_gh.PersonalAccessToken))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _gh.PersonalAccessToken);
        return http;
    }

    private string RepoBaseUrl() =>
        $"{_gh.ApiBaseUrl.TrimEnd('/')}/repos/{Uri.EscapeDataString(_gh.Owner)}/{Uri.EscapeDataString(_gh.Repository)}";
}
