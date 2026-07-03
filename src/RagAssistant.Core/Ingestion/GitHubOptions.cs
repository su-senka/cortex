namespace RagAssistant.Core.Ingestion;

public sealed class GitHubOptions
{
    // GitHub API root — override for GitHub Enterprise (e.g. https://ghe.example.com/api/v3).
    public string ApiBaseUrl { get; set; } = "https://api.github.com";
    public string Owner { get; set; } = "";
    public string Repository { get; set; } = "";
    // Folder path within the repo to scan, e.g. "/" or "/docs"
    public string Path { get; set; } = "/";
    public string Branch { get; set; } = "main";
    public string PersonalAccessToken { get; set; } = "";
    // Optional HMAC-SHA256 secret — if set, incoming webhook payloads are validated
    // against the X-Hub-Signature-256 header GitHub sends.
    public string WebhookSecret { get; set; } = "";
}
