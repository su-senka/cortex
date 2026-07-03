using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagAssistant.Core.Models;

namespace RagAssistant.Core;

public sealed class RagQueryService(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IChatClient chatClient,
    VectorStoreCollection<string, DocumentChunk> collection,
    IOptions<RagOptions> options)
{
    private readonly RagOptions _options = options.Value;

    private const string SystemPrompt =
        "You are a helpful assistant for internal documentation. " +
        "Answer the user's question based ONLY on the provided document excerpts. " +
        "Each excerpt is prefixed with its number, e.g. [1] or [2]. " +
        "After each sentence that draws on a numbered source, append the citation marker [^N] " +
        "where N is the source number — for example: 'Split tunneling must be disabled. [^1]' " +
        "Only cite sources you actually used. " +
        "If the answer is not found in the documents, say so explicitly — " +
        "do not use knowledge outside the provided context.";

    /// <summary>
    /// Embeds the question, retrieves the top-k chunks, and returns:
    ///   • Sources — the retrieved chunks (available before the LLM call starts)
    ///   • AnswerStream — lazy async enumerable that streams LLM tokens on demand
    /// </summary>
    /// <param name="history">
    /// Optional prior conversation turns (user/assistant alternating). They are inserted
    /// into the LLM prompt between the system message and the current user message so the
    /// model can refer back to earlier exchanges. Vector search always runs on the current
    /// question only — history is not re-embedded.
    /// </param>
    public async Task<RagQueryResult> QueryAsync(
        string question,
        IReadOnlyList<ChatMessage>? history = null,
        CancellationToken ct = default)
    {
        using var queryActivity = Telemetry.ActivitySource.StartActivity("rag.query");
        queryActivity?.SetTag("rag.question_length", question.Length);

        // Step 1: embed the question.
        ReadOnlyMemory<float> queryVector;
        using (Telemetry.ActivitySource.StartActivity("rag.embed"))
        {
            var sw = Stopwatch.GetTimestamp();
            var embeddings = await embedder.GenerateAsync([question], cancellationToken: ct);
            queryVector = embeddings[0].Vector;
            Telemetry.EmbeddingDurationMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
        }

        // Step 2: search the vector store.
        var hits = new List<VectorSearchResult<DocumentChunk>>();
        using (var searchActivity = Telemetry.ActivitySource.StartActivity("rag.vector_search"))
        {
            var sw = Stopwatch.GetTimestamp();
            await foreach (var hit in collection.SearchAsync(queryVector, _options.TopK, cancellationToken: ct))
                hits.Add(hit);
            Telemetry.VectorSearchDurationMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
            searchActivity?.SetTag("rag.results_count", hits.Count);
        }

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

        queryActivity?.SetTag("rag.sources_found", sources.Count);
        Telemetry.QueriesTotal.Add(1);

        // Step 4: compose the prompt and return a lazy stream for the LLM response.
        // Sources are available immediately; the answer stream is only consumed by the caller.
        var messages = BuildMessages(question, hits, history);

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
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    private static List<ChatMessage> BuildMessages(
        string question,
        IReadOnlyList<VectorSearchResult<DocumentChunk>> hits,
        IReadOnlyList<ChatMessage>? history)
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

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
        };

        // Inject prior turns so the model has conversational context.
        if (history is { Count: > 0 })
            messages.AddRange(history);

        messages.Add(new(ChatRole.User, userContent));
        return messages;
    }
}
