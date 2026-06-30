using Microsoft.Extensions.VectorData;

namespace RagAssistant.Core.Models;

public sealed class DocumentChunk
{
    // Deterministic key: "{relativeFilePath}#{chunkIndex}" — upserts act as idempotent
    // updates, and we can reconstruct or delete specific chunk keys without a full scan.
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    // The chunk text, prefixed with its heading breadcrumb for context during retrieval.
    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceFile { get; set; } = string.Empty;

    [VectorStoreData]
    public string Title { get; set; } = string.Empty;

    // Comma-separated tag list from front matter; stored for display, not filtering.
    [VectorStoreData]
    public string Tags { get; set; } = string.Empty;

    [VectorStoreData]
    public string LastVerified { get; set; } = string.Empty;

    // Full heading breadcrumb, e.g. "Getting Started > Installation > Windows".
    [VectorStoreData]
    public string SectionHeading { get; set; } = string.Empty;

    [VectorStoreData]
    public int ChunkIndex { get; set; }

    // nomic-embed-text produces 768-dimensional vectors; update Dimensions if you
    // switch embedding models (model and dimension must always agree).
    [VectorStoreVector(768, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
