namespace RagAssistant.Core.Ingestion;

public sealed class AzureDevOpsOptions
{
    // Base URL of the ADO Server including the collection, e.g. https://ado/tfs/DefaultCollection
    public string BaseUrl { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
    // Folder path within the repo to scan, e.g. "/" or "/docs"
    public string Path { get; set; } = "/";
    public string Branch { get; set; } = "main";
    public string PersonalAccessToken { get; set; } = "";
    // Optional HMAC-SHA1 secret — if set, incoming webhook payloads are validated
    // against the X-Hub-Signature header that ADO Server sends.
    public string WebhookSecret { get; set; } = "";
}
