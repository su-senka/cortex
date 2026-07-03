namespace RagAssistant.Core.Ingestion;

/// <summary>
/// A remote document source (Azure DevOps, GitHub, …). Connectors stream the
/// current set of documents; <see cref="DocumentSourceSynchronizer"/> mirrors
/// them into a local cache folder and hands off to the shared ingestion pipeline.
/// </summary>
public interface IDocumentSource
{
    /// <summary>Short name used in logs and for the local cache subfolder.</summary>
    string Name { get; }

    /// <summary>False when required settings (base URL, repo, …) are missing.</summary>
    bool IsConfigured { get; }

    /// <summary>Streams every document currently present in the source.</summary>
    IAsyncEnumerable<RawDocument> SyncAsync(CancellationToken ct = default);
}

/// <param name="RelativePath">Source-relative path, forward slashes (e.g. "guides/vpn.md").</param>
/// <param name="Content">Full document text.</param>
public sealed record RawDocument(string RelativePath, string Content);
