using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using NSubstitute;
using RagAssistant.Core;
using RagAssistant.Core.Models;
using RagAssistant.Tests.Support;

namespace RagAssistant.Tests;

public sealed class RagQueryServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FullTextIndex _fullText;
    private readonly VectorStoreCollection<string, DocumentChunk> _collection;
    private readonly IChatClient _chat;

    private List<string> _embeddedTexts = [];
    private List<ChatMessage>? _streamedMessages;
    private string _rerankReply = "[]";

    public RagQueryServiceTests()
    {
        _fullText = new FullTextIndex($"Data Source={_dir.File("fts.db")}");
        _fullText.EnsureCreatedAsync().GetAwaiter().GetResult();

        _collection = Substitute.For<VectorStoreCollection<string, DocumentChunk>>();

        _chat = Substitute.For<IChatClient>();
        _chat.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _streamedMessages = ci.Arg<IEnumerable<ChatMessage>>().ToList();
                return Stubs.StreamedText("Answer.");
            });
        _chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ChatResponse(new ChatMessage(ChatRole.Assistant, _rerankReply)));
    }

    public void Dispose() => _dir.Dispose();

    private RagQueryService CreateService(RagOptions? options = null)
    {
        options ??= new RagOptions();
        return new RagQueryService(
            Stubs.Embedder(texts => _embeddedTexts = texts.ToList()),
            _chat, _collection, _fullText, Options.Create(options));
    }

    private void VectorSearchReturns(params DocumentChunk[] chunks) =>
        _collection.SearchAsync(
                Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<int>(),
                Arg.Any<VectorSearchOptions<DocumentChunk>?>(), Arg.Any<CancellationToken>())
            .Returns(Stubs.SearchResults(chunks));

    [Fact]
    public async Task Query_BuildsSourcesFromRetrievedChunks_BeforeStreaming()
    {
        VectorSearchReturns(
            Stubs.Chunk("a.md#0", "Content A", title: "Doc A", sourceFile: "a.md", sectionHeading: "Intro"),
            Stubs.Chunk("b.md#0", "Content B", title: "Doc B", sourceFile: "b.md"));

        var result = await CreateService(new RagOptions { HybridSearch = false }).QueryAsync("question");

        // Sources are available deterministically, without consuming the answer stream.
        result.Sources.Should().HaveCount(2);
        result.Sources[0].SourceIndex.Should().Be(1);
        result.Sources[0].SourceFile.Should().Be("a.md");
        result.Sources[0].ChunkText.Should().Be("Content A");
        result.Sources[1].SourceIndex.Should().Be(2);
        _streamedMessages.Should().BeNull("the LLM must not be called until the stream is consumed");
    }

    [Fact]
    public async Task Query_NoResults_StillAnswersWithEmptyContext()
    {
        VectorSearchReturns();

        var result = await CreateService().QueryAsync("question");
        var answer = string.Concat(await result.AnswerStream.ToListAsync());

        result.Sources.Should().BeEmpty();
        answer.Should().Be("Answer.");
        _streamedMessages!.Last().Text.Should().Contain("(No relevant documentation found.)");
    }

    [Fact]
    public async Task HybridSearch_IncludesBm25OnlyHits()
    {
        var vectorChunk = Stubs.Chunk("a.md#0", "Vector-matched content");
        var keywordChunk = Stubs.Chunk("b.md#0", "Explains the ACRONYM in detail");

        VectorSearchReturns(vectorChunk);
        await _fullText.UpsertAsync([("b.md#0", keywordChunk.Content)]);
        _collection.GetAsync("b.md#0", Arg.Any<RecordRetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(keywordChunk);

        var result = await CreateService().QueryAsync("what is the ACRONYM");

        result.Sources.Select(s => s.SourceFile).Should().BeEquivalentTo("a.md", "b.md");
    }

    [Fact]
    public async Task HybridSearch_RanksChunkFoundByBothLegsFirst()
    {
        var both = Stubs.Chunk("both.md#0", "The VPN gateway configuration.", sourceFile: "both.md");
        var vectorOnly = Stubs.Chunk("vec.md#0", "Unrelated semantic match.", sourceFile: "vec.md");

        VectorSearchReturns(both, vectorOnly);
        await _fullText.UpsertAsync([("both.md#0", both.Content)]);

        var result = await CreateService().QueryAsync("VPN gateway");

        // "both" scores 1/(k+1) from each leg; the others only score once.
        result.Sources[0].SourceFile.Should().Be("both.md");
        result.Sources[0].Score.Should().Be(1.0, "fused scores are normalised to the best hit");
    }

    [Fact]
    public async Task HybridSearch_SkipsFtsRowsWithoutVectorRecord()
    {
        VectorSearchReturns(Stubs.Chunk("a.md#0", "Valid content"));
        await _fullText.UpsertAsync([("orphan.md#0", "Orphaned quixotic row")]);
        _collection.GetAsync("orphan.md#0", Arg.Any<RecordRetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns((DocumentChunk?)null);

        var result = await CreateService().QueryAsync("quixotic content");

        result.Sources.Should().ContainSingle().Which.SourceFile.Should().Be("a.md");
    }

    [Fact]
    public async Task TagFilter_LimitsSourcesToMatchingChunks()
    {
        VectorSearchReturns(
            Stubs.Chunk("net.md#0", "Networking doc", tags: "network, vpn", sourceFile: "net.md"),
            Stubs.Chunk("hr.md#0", "HR doc", tags: "hr", sourceFile: "hr.md"));

        var result = await CreateService(new RagOptions { HybridSearch = false })
            .QueryAsync("question", tagFilter: "vpn");

        result.Sources.Should().ContainSingle().Which.SourceFile.Should().Be("net.md");
    }

    [Fact]
    public async Task TagFilter_IsCaseInsensitive()
    {
        VectorSearchReturns(Stubs.Chunk("a.md#0", "Doc", tags: "Network, VPN"));

        var result = await CreateService(new RagOptions { HybridSearch = false })
            .QueryAsync("question", tagFilter: "vpn");

        result.Sources.Should().HaveCount(1);
    }

    [Fact]
    public async Task History_LongConversation_IsTruncatedWithNote()
    {
        VectorSearchReturns(Stubs.Chunk("a.md#0"));
        var history = Enumerable.Range(0, 10)
            .Select(i => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"turn {i}"))
            .ToList();

        var service = CreateService(new RagOptions { HybridSearch = false, MaxHistoryMessages = 4 });
        var result = await service.QueryAsync("question", history);
        await result.AnswerStream.ToListAsync();

        // system prompt + truncation note + 4 recent turns + final user message
        _streamedMessages.Should().HaveCount(7);
        _streamedMessages![1].Text.Should().Contain("6 earlier message(s)");
        _streamedMessages[2].Text.Should().Be("turn 6");
        _streamedMessages[5].Text.Should().Be("turn 9");
    }

    [Fact]
    public async Task History_ShortConversation_IsSentVerbatim()
    {
        VectorSearchReturns(Stubs.Chunk("a.md#0"));
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "earlier question"),
            new(ChatRole.Assistant, "earlier answer"),
        };

        var result = await CreateService(new RagOptions { HybridSearch = false })
            .QueryAsync("question", history);
        await result.AnswerStream.ToListAsync();

        // system prompt + 2 turns + final user message; no truncation note
        _streamedMessages.Should().HaveCount(4);
        _streamedMessages![1].Text.Should().Be("earlier question");
    }

    [Fact]
    public async Task Reranking_AppliesModelOrder()
    {
        VectorSearchReturns(
            Stubs.Chunk("first.md#0", "Vector's top pick", sourceFile: "first.md"),
            Stubs.Chunk("second.md#0", "Actually more relevant", sourceFile: "second.md"));
        _rerankReply = "[2, 1]";

        var result = await CreateService(new RagOptions
        {
            HybridSearch = false,
            TopK = 1,
            Reranking = new RerankingOptions { Enabled = true, CandidateMultiplier = 2 },
        }).QueryAsync("question");

        result.Sources.Should().ContainSingle().Which.SourceFile.Should().Be("second.md");
    }

    [Fact]
    public async Task Reranking_UnparseableReply_KeepsOriginalOrder()
    {
        VectorSearchReturns(
            Stubs.Chunk("first.md#0", sourceFile: "first.md"),
            Stubs.Chunk("second.md#0", sourceFile: "second.md"));
        _rerankReply = "I think the second one is best!";

        var result = await CreateService(new RagOptions
        {
            HybridSearch = false,
            TopK = 1,
            Reranking = new RerankingOptions { Enabled = true, CandidateMultiplier = 2 },
        }).QueryAsync("question");

        result.Sources.Should().ContainSingle().Which.SourceFile.Should().Be("first.md");
    }

    [Fact]
    public async Task Hyde_ShortQuestion_EmbedsHypotheticalAnswer()
    {
        VectorSearchReturns(Stubs.Chunk("a.md#0"));
        _rerankReply = "The VPN requires the corporate client and MFA.";

        await CreateService(new RagOptions
        {
            HybridSearch = false,
            Hyde = new HydeOptions { Enabled = true, MaxQuestionLength = 100 },
        }).QueryAsync("VPN?");

        _embeddedTexts.Should().ContainSingle();
        _embeddedTexts[0].Should().StartWith("VPN?").And.Contain("corporate client");
    }

    [Fact]
    public async Task Hyde_LongQuestion_EmbedsQuestionDirectly()
    {
        VectorSearchReturns(Stubs.Chunk("a.md#0"));
        var longQuestion = new string('q', 150);

        await CreateService(new RagOptions
        {
            HybridSearch = false,
            Hyde = new HydeOptions { Enabled = true, MaxQuestionLength = 100 },
        }).QueryAsync(longQuestion);

        _embeddedTexts[0].Should().Be(longQuestion);
        await _chat.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}

file static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}
