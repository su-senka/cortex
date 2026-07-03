namespace RagAssistant.Core;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ChatModel { get; set; } = "qwen2.5:7b-instruct-q4_K_M";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
}

public sealed class RagOptions
{
    public string DocsFolder { get; set; } = "docs";
    public string VectorDbPath { get; set; } = "rag_store.db";

    // Default 1500 chars — large enough to hold several paragraphs, small enough
    // that the embedding represents a coherent sub-topic.
    public int ChunkSize { get; set; } = 1500;

    // 150-char overlap prevents a sentence that straddles a chunk boundary from
    // being completely absent from both chunks' embeddings.
    public int ChunkOverlap { get; set; } = 150;

    // Top-5 gives the model enough context while keeping the prompt compact.
    public int TopK { get; set; } = 5;

    // Combine vector search with BM25 keyword search (SQLite FTS5) and merge the
    // two result lists with Reciprocal Rank Fusion. Improves recall on exact
    // keyword / acronym queries that cosine similarity alone misses.
    public bool HybridSearch { get; set; } = true;

    // RRF constant: score = Σ 1/(k + rank). 60 is the standard from the RRF paper;
    // larger values flatten the difference between top and bottom ranks.
    public int RrfK { get; set; } = 60;

    // Sliding-window history limit — only the most recent N messages are sent to
    // the LLM; older turns are replaced by a one-line truncation marker so long
    // conversations don't blow the context window.
    public int MaxHistoryMessages { get; set; } = 12;

    public HydeOptions Hyde { get; set; } = new();
    public RerankingOptions Reranking { get; set; } = new();
}

// HyDE (Hypothetical Document Embedding): for short/ambiguous questions, generate
// a hypothetical answer first and embed that for retrieval — the hypothetical text
// is phrased like documentation, so it lands closer to the target chunks.
public sealed class HydeOptions
{
    // Off by default: adds one extra LLM round-trip per query.
    public bool Enabled { get; set; } = false;

    // Only questions at or below this length use HyDE — longer questions already
    // carry enough signal for direct embedding.
    public int MaxQuestionLength { get; set; } = 100;
}

// LLM-based reranking: retrieve TopK × CandidateMultiplier candidates, ask the chat
// model to order them by relevance, and keep the best TopK.
public sealed class RerankingOptions
{
    // Off by default: adds one extra LLM round-trip per query.
    public bool Enabled { get; set; } = false;

    public int CandidateMultiplier { get; set; } = 2;
}
