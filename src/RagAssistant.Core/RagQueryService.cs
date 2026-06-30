using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagAssistant.Core.Models;

namespace RagAssistant.Core;

public sealed class RagQueryService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IChatClient _chatClient;
    private readonly VectorStoreCollection<string, DocumentChunk> _collection;
    private readonly RagOptions _options;

    private const string SystemPrompt =
        "You are a helpful assistant for internal documentation. " +
        "Answer the user's question based ONLY on the provided document excerpts. " +
        "Each excerpt is prefixed with its number, e.g. [1] or [2]. " +
        "After each sentence that draws on a numbered source, append the citation marker [^N] " +
        "where N is the source number — for example: 'Split tunneling must be disabled. [^1]' " +
        "Only cite sources you actually used. " +
        "If the answer is not found in the documents, say so explicitly — " +
        "do not use knowledge outside the provided context.";

    public RagQueryService(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IChatClient chatClient,
        VectorStoreCollection<string, DocumentChunk> collection,
        IOptions<RagOptions> options)
    {
        _embedder = embedder;
        _chatClient = chatClient;
        _collection = collection;
        _options = options.Value;
    }

    /// <summary>
    /// Embeds the question, retrieves the top-k chunks, and returns:
    ///   • Sources — the retrieved chunks (available before the LLM call starts)
    ///   • AnswerStream — lazy async enumerable that streams LLM tokens on demand
    /// </summary>
    public async Task<RagQueryResult> QueryAsync(string question, CancellationToken ct = default)
    {
        // Step 1: embed the question.
        var embeddings = await _embedder.GenerateAsync([question], cancellationToken: ct);
        var queryVector = embeddings[0].Vector;

        // Step 2: search the vector store.
        var hits = new List<VectorSearchResult<DocumentChunk>>();
        await foreach (var hit in _collection.SearchAsync(queryVector, _options.TopK, cancellationToken: ct))
            hits.Add(hit);

        // Step 3: build source list from search results — deterministic, not derived from LLM output.
        var sources = hits
            .Select((h, i) => new SourceReference(
                i + 1,
                h.Record.SourceFile,
                h.Record.Title,
                h.Record.SectionHeading,
                h.Score ?? 0,
                h.Record.Content))
            .ToList();

        // Step 4: compose the prompt and return a lazy stream for the LLM response.
        // Sources are available immediately; the answer stream is only consumed by the caller.
        var messages = BuildMessages(question, hits);

        return new RagQueryResult
        {
            Sources = sources,
            AnswerStream = StreamAnswerAsync(messages, ct),
        };
    }

    private async IAsyncEnumerable<string> StreamAnswerAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    private static List<ChatMessage> BuildMessages(
        string question,
        IReadOnlyList<VectorSearchResult<DocumentChunk>> hits)
    {
        var context = hits.Count == 0
            ? "(No relevant documentation found.)"
            : string.Join("\n\n---\n\n", hits.Select((h, i) =>
                $"[{i + 1}] {h.Record.Title}" +
                (string.IsNullOrEmpty(h.Record.SectionHeading)
                    ? string.Empty
                    : $" > {h.Record.SectionHeading}") +
                $"\n\n{h.Record.Content}"));

        var userContent =
            $"""
            Use the documentation excerpts below to answer the question.
            If the answer is not in the documents, say so clearly.

            === DOCUMENTATION ===
            {context}
            === END ===

            Question: {question}
            """;

        return
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, userContent),
        ];
    }
}
