using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RagAssistant.Core;

public static class Telemetry
{
    public const string ServiceName = "RagAssistant";

    public static readonly ActivitySource ActivitySource = new(ServiceName, "1.0.0");

    private static readonly Meter Meter = new(ServiceName, "1.0.0");

    public static readonly Counter<long> QueriesTotal =
        Meter.CreateCounter<long>("rag.queries.total", description: "Total RAG queries processed");

    public static readonly Counter<long> IngestionsTotal =
        Meter.CreateCounter<long>("rag.ingestions.total", description: "Total ingestion runs");

    public static readonly Counter<long> ChunksUpserted =
        Meter.CreateCounter<long>("rag.chunks.upserted", description: "Total chunks upserted");

    public static readonly Counter<long> ChunksDeleted =
        Meter.CreateCounter<long>("rag.chunks.deleted", description: "Total chunks deleted as stale");

    public static readonly Histogram<double> EmbeddingDurationMs =
        Meter.CreateHistogram<double>("rag.embedding.duration", unit: "ms", description: "Embedding generation duration");

    public static readonly Histogram<double> VectorSearchDurationMs =
        Meter.CreateHistogram<double>("rag.vector_search.duration", unit: "ms", description: "Vector search duration");
}