using FluentAssertions;
using RagAssistant.Core;
using RagAssistant.Tests.Support;

namespace RagAssistant.Tests;

public sealed class FullTextIndexTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FullTextIndex _index;

    public FullTextIndexTests()
    {
        _index = new FullTextIndex($"Data Source={_dir.File("fts.db")}");
        _index.EnsureCreatedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Search_FindsDocumentContainingKeyword()
    {
        await _index.UpsertAsync([
            ("vpn.md#0", "Connect to the VPN using the corporate client."),
            ("tls.md#0", "Rotate the TLS certificate before it expires."),
        ]);

        // Stopwords ("the") may OR-match other documents; BM25 must still rank
        // the keyword-bearing document first.
        var results = await _index.SearchAsync("how do I set up the VPN", top: 5);

        results.Should().NotBeEmpty();
        results[0].Should().Be("vpn.md#0");
    }

    [Fact]
    public async Task Search_SurvivesPunctuationAndQuotes()
    {
        await _index.UpsertAsync([("vpn.md#0", "VPN setup instructions.")]);

        // Raw FTS5 would choke on quotes/operators — the query must be sanitised.
        var results = await _index.SearchAsync("""what's the "VPN" setup?! (urgent) -now""", top: 5);

        results.Should().Contain("vpn.md#0");
    }

    [Fact]
    public async Task Search_EmptyOrTrivialQuery_ReturnsEmpty()
    {
        await _index.UpsertAsync([("a.md#0", "Some content here.")]);

        (await _index.SearchAsync("?!", top: 5)).Should().BeEmpty();
        (await _index.SearchAsync("", top: 5)).Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_SameKeyTwice_KeepsSingleRow()
    {
        await _index.UpsertAsync([("a.md#0", "Original text about kubernetes.")]);
        await _index.UpsertAsync([("a.md#0", "Updated text about kubernetes.")]);

        var results = await _index.SearchAsync("kubernetes", top: 10);

        results.Should().ContainSingle().Which.Should().Be("a.md#0");
    }

    [Fact]
    public async Task Upsert_ReplacesContent()
    {
        await _index.UpsertAsync([("a.md#0", "Original text about postgres.")]);
        await _index.UpsertAsync([("a.md#0", "Updated text about redis.")]);

        (await _index.SearchAsync("postgres", top: 10)).Should().BeEmpty();
        (await _index.SearchAsync("redis", top: 10)).Should().Contain("a.md#0");
    }

    [Fact]
    public async Task Delete_RemovesKeys()
    {
        await _index.UpsertAsync([
            ("a.md#0", "Document about grafana."),
            ("a.md#1", "More about grafana dashboards."),
        ]);

        await _index.DeleteAsync(["a.md#0", "a.md#1"]);

        (await _index.SearchAsync("grafana", top: 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_RespectsTopLimit()
    {
        await _index.UpsertAsync(Enumerable.Range(0, 10)
            .Select(i => ($"doc.md#{i}", $"Chunk {i} mentions elasticsearch."))
            .ToList());

        var results = await _index.SearchAsync("elasticsearch", top: 3);

        results.Should().HaveCount(3);
    }
}
