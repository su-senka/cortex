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
}
