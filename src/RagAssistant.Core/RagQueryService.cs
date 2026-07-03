using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagAssistant.Core.Models;

namespace RagAssistant.Core;

public sealed class RagQueryService(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IChatClient chatClient,
    VectorStoreCollection<string, DocumentChunk> collection,
    FullTextIndex fullText,
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

    private const string HydePrompt =
        "Write a short internal-documentation excerpt (3–5 sentences) that would plausibly " +
        "answer the user's question. Write it as documentation prose, not as an answer to " +
        "the user; do not mention that it is hypothetical.";

    /// <summary>
    /// Retrieves the most relevant chunks (vector + optional BM25 hybrid, optional HyDE
    /// and reranking), and returns:
    ///   • Sources — the retrieved chunks (available before the LLM call starts)
    ///   • AnswerStream — lazy async enumerable that streams LLM tokens on demand
    /// </summary>
    /// <param name="history">
    /// Optional prior conversation turns (user/assistant alternating). Only the most
    /// recent Rag:MaxHistoryMessages are sent to the LLM; retrieval always runs on the
    /// current question only — history is not re-embedded.
    /// </param>
    /// <param name="tagFilter">
    /// Optional tag scope from the front-end: only chunks whose front-matter tags
    /// contain this value are eligible.
    /// </param>
    public async Task<RagQueryResult> QueryAsync(
        string question,
        IReadOnlyList<ChatMessage>? history = null,
        string? tagFilter = null,
        CancellationToken ct = default)
    {
        using var queryActivity = Telemetry.ActivitySource.StartActivity("rag.query");
        queryActivity?.SetTag("rag.question_length", question.Length);
        queryActivity?.SetTag("rag.tag_filter", tagFilter ?? "");

        // Step 0 (optional): HyDE — embed a hypothetical answer instead of the bare
        // question when the question is short enough to be ambiguous.
        var retrievalText = question;
        if (_options.Hyde.Enabled && question.Length <= _options.Hyde.MaxQuestionLength)
            retrievalText = await GenerateHypotheticalAsync(question, ct);

        // Step 1: embed the retrieval text.
        ReadOnlyMemory<float> queryVector;
        using (Telemetry.ActivitySource.StartActivity("rag.embed"))
        {
            var sw = Stopwatch.GetTimestamp();
            var embeddings = await embedder.GenerateAsync([retrievalText], cancellationToken: ct);
            queryVector = embeddings[0].Vector;
            Telemetry.EmbeddingDurationMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
        }

        // Candidate budget: more when reranking will trim the list afterwards, and
        // more again when a tag filter discards an unknown share of the results.
        var candidateCount = _options.TopK *
            (_options.Reranking.Enabled ? Math.Max(1, _options.Reranking.CandidateMultiplier) : 1);
        var fetchCount = string.IsNullOrWhiteSpace(tagFilter) ? candidateCount : candidateCount * 4;

        // Step 2: vector search.
        var vectorHits = new List<VectorSearchResult<DocumentChunk>>();
        using (var searchActivity = Telemetry.ActivitySource.StartActivity("rag.vector_search"))
        {
            var sw = Stopwatch.GetTimestamp();
            await foreach (var hit in collection.SearchAsync(queryVector, fetchCount, cancellationToken: ct))
                vectorHits.Add(hit);
            Telemetry.VectorSearchDurationMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
            searchActivity?.SetTag("rag.results_count", vectorHits.Count);
        }

        // Step 3: optional BM25 leg + Reciprocal Rank Fusion.
        var candidates = _options.HybridSearch
            ? await FuseWithBm25Async(question, vectorHits, fetchCount, ct)
            : vectorHits.Select(h => (h.Record, Score: h.Score ?? 0)).ToList();

        // Step 4: metadata pre-filter (tag scope from the front-end).
        if (!string.IsNullOrWhiteSpace(tagFilter))
            candidates = candidates.Where(c => HasTag(c.Record.Tags, tagFilter)).ToList();

        if (candidates.Count > candidateCount)
            candidates = candidates[..candidateCount];

        // Step 5: optional LLM reranking, then final TopK cut.
        if (_options.Reranking.Enabled && candidates.Count > _options.TopK)
            candidates = await RerankAsync(question, candidates, ct);

        if (candidates.Count > _options.TopK)
            candidates = candidates[.._options.TopK];

        // Step 6: build source list — deterministic, not derived from LLM output.
        var sources = candidates
            .Select((c, i) => new SourceReference(
                i + 1,
                c.Record.SourceFile,
                c.Record.Title,
                c.Record.SectionHeading,
                c.Score,
                c.Record.Content))
            .ToList();

        queryActivity?.SetTag("rag.sources_found", sources.Count);
        Telemetry.QueriesTotal.Add(1);

        // Step 7: compose the prompt and return a lazy stream for the LLM response.
        var messages = BuildMessages(question, candidates.Select(c => c.Record).ToList(), history);

        return new RagQueryResult
        {
            Sources = sources,
            AnswerStream = StreamAnswerAsync(messages, ct),
        };
    }

    // ── Hybrid search ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Merges the vector result list with a BM25 keyword search using Reciprocal
    /// Rank Fusion: score(d) = Σ 1/(RrfK + rank). Fused scores are normalised so
    /// the best candidate is 1.0 — the UI shows them as relative percentages.
    /// </summary>
    private async Task<List<(DocumentChunk Record, double Score)>> FuseWithBm25Async(
        string question,
        List<VectorSearchResult<DocumentChunk>> vectorHits,
        int fetchCount,
        CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("rag.bm25_search");

        var bm25Keys = await fullText.SearchAsync(question, fetchCount, ct);
        activity?.SetTag("rag.results_count", bm25Keys.Count);

        var fused = new Dictionary<string, double>();
        for (int i = 0; i < vectorHits.Count; i++)
            fused[vectorHits[i].Record.Key] =
                fused.GetValueOrDefault(vectorHits[i].Record.Key) + 1.0 / (_options.RrfK + i + 1);
        for (int i = 0; i < bm25Keys.Count; i++)
            fused[bm25Keys[i]] =
                fused.GetValueOrDefault(bm25Keys[i]) + 1.0 / (_options.RrfK + i + 1);

        if (fused.Count == 0) return [];

        var byKey = vectorHits.ToDictionary(h => h.Record.Key, h => h.Record);
        var maxScore = fused.Values.Max();

        var result = new List<(DocumentChunk, double)>();
        foreach (var (key, score) in fused.OrderByDescending(kv => kv.Value))
        {
            // BM25-only hits aren't in the vector result set — load them by key.
            if (!byKey.TryGetValue(key, out var record))
                record = await collection.GetAsync(key, cancellationToken: ct);
            if (record is null) continue; // FTS row outlived the vector record

            result.Add((record, score / maxScore));
        }
        return result;
    }

    private static bool HasTag(string tags, string filter) =>
        tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));

    // ── HyDE ──────────────────────────────────────────────────────────────────────

    private async Task<string> GenerateHypotheticalAsync(string question, CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("rag.hyde");
        try
        {
            var response = await chatClient.GetResponseAsync(
                [new(ChatRole.System, HydePrompt), new(ChatRole.User, question)],
                new ChatOptions { MaxOutputTokens = 200 },
                ct);

            var hypothetical = response.Text;
            if (string.IsNullOrWhiteSpace(hypothetical))
                return question;

            // Keep the original question so its exact terms still contribute to the embedding.
            return $"{question}\n\n{hypothetical}";
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            activity?.SetTag("rag.hyde_failed", true);
            return question; // HyDE is best-effort — fall back to the raw question
        }
    }

    // ── Reranking ─────────────────────────────────────────────────────────────────

    private static readonly Regex JsonArrayRegex = new(@"\[[\d,\s]*\]", RegexOptions.Compiled);

    /// <summary>
    /// Asks the chat model to order the candidates by relevance to the question.
    /// Best-effort: if the model's output can't be parsed, the original (RRF/vector)
    /// order is kept. Candidates the model omits are appended in original order.
    /// </summary>
    private async Task<List<(DocumentChunk Record, double Score)>> RerankAsync(
        string question,
        List<(DocumentChunk Record, double Score)> candidates,
        CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("rag.rerank");
        activity?.SetTag("rag.candidates", candidates.Count);

        var excerpts = string.Join("\n\n", candidates.Select((c, i) =>
        {
            var text = c.Record.Content;
            if (text.Length > 500) text = text[..500];
            return $"[{i + 1}] {text}";
        }));

        var prompt =
            $"""
            You are reranking search results. Order the numbered excerpts below from most
            to least relevant to the question. Respond with ONLY a JSON array of excerpt
            numbers, e.g. [3,1,2] — no explanation.

            Question: {question}

            {excerpts}
            """;

        try
        {
            var response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { MaxOutputTokens = 100 },
                ct);

            var match = JsonArrayRegex.Match(response.Text);
            if (!match.Success) return candidates;

            var order = JsonSerializer.Deserialize<List<int>>(match.Value) ?? [];
            var reranked = new List<(DocumentChunk, double)>();
            var seen = new HashSet<int>();

            foreach (var n in order)
            {
                var idx = n - 1;
                if (idx < 0 || idx >= candidates.Count || !seen.Add(idx)) continue;
                reranked.Add(candidates[idx]);
            }
            for (int i = 0; i < candidates.Count; i++)
                if (seen.Add(i)) reranked.Add(candidates[i]);

            return reranked;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            activity?.SetTag("rag.rerank_failed", true);
            return candidates; // reranking is best-effort
        }
    }

    // ── Prompt assembly & streaming ───────────────────────────────────────────────

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

    private List<ChatMessage> BuildMessages(
        string question,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<ChatMessage>? history)
    {
        var context = chunks.Count == 0
            ? "(No relevant documentation found.)"
            : string.Join("\n\n---\n\n", chunks.Select((c, i) =>
                $"[{i + 1}] {c.Title}" +
                (string.IsNullOrEmpty(c.SectionHeading)
                    ? string.Empty
                    : $" > {c.SectionHeading}") +
                $"\n\n{c.Content}"));

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

        // Inject prior turns so the model has conversational context — sliding
        // window: only the most recent MaxHistoryMessages are sent verbatim, older
        // turns are collapsed into a truncation note.
        if (history is { Count: > 0 })
        {
            var max = Math.Max(2, _options.MaxHistoryMessages);
            if (history.Count > max)
            {
                messages.Add(new(ChatRole.System,
                    $"(Context note: {history.Count - max} earlier message(s) in this conversation " +
                    "were omitted to keep the context compact. Ask the user to restate anything they " +
                    "reference from that omitted portion if it is unclear.)"));
                history = history.Skip(history.Count - max).ToList();
            }
            messages.AddRange(history);
        }

        messages.Add(new(ChatRole.User, userContent));
        return messages;
    }
}
