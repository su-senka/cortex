using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using NSubstitute;
using RagAssistant.Core;
using RagAssistant.Core.Ingestion;
using RagAssistant.Core.Models;
using RagAssistant.Tests.Support;

namespace RagAssistant.Tests;

public sealed class MarkdownIngestionServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _docsFolder;
    private readonly List<DocumentChunk> _upserted = [];
    private readonly List<string> _deletedKeys = [];
    private readonly FullTextIndex _fullText;

    public MarkdownIngestionServiceTests()
    {
        _docsFolder = _dir.File("docs");
        Directory.CreateDirectory(_docsFolder);
        _fullText = new FullTextIndex($"Data Source={_dir.File("test.db")}");
    }

    public void Dispose() => _dir.Dispose();

    private MarkdownIngestionService CreateService(int chunkSize = 1500, int chunkOverlap = 150)
    {
        var collection = Substitute.For<VectorStoreCollection<string, DocumentChunk>>();
        collection.UpsertAsync(Arg.Any<IEnumerable<DocumentChunk>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _upserted.AddRange(ci.Arg<IEnumerable<DocumentChunk>>()));
        collection.DeleteAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _deletedKeys.AddRange(ci.Arg<IEnumerable<string>>()));

        var options = Options.Create(new RagOptions
        {
            DocsFolder = _docsFolder,
            VectorDbPath = _dir.File("test.db"),
            ChunkSize = chunkSize,
            ChunkOverlap = chunkOverlap,
        });

        return new MarkdownIngestionService(
            Stubs.Embedder(), collection, _fullText, options,
            NullLogger<MarkdownIngestionService>.Instance);
    }

    private Task WriteDoc(string name, string content) =>
        File.WriteAllTextAsync(Path.Combine(_docsFolder, name), content);

    [Fact]
    public async Task Ingest_ParsesFrontMatter_AndBuildsHeadingBreadcrumbs()
    {
        await WriteDoc("vpn.md",
            """
            ---
            title: VPN Guide
            tags: [network, vpn]
            last_verified: 2026-01-15
            ---
            Intro paragraph before any heading.

            # Setup

            General setup steps.

            ## macOS

            macOS-specific steps.
            """);

        await CreateService().IngestAllAsync();

        _upserted.Should().HaveCount(3);

        _upserted.Should().AllSatisfy(c =>
        {
            c.Title.Should().Be("VPN Guide");
            c.Tags.Should().Be("network, vpn");
            c.LastVerified.Should().Be("2026-01-15");
            c.SourceFile.Should().Be("vpn.md");
        });

        // Deterministic keys: "{relativePath}#{chunkIndex}"
        _upserted.Select(c => c.Key).Should().Equal("vpn.md#0", "vpn.md#1", "vpn.md#2");

        _upserted[0].SectionHeading.Should().BeEmpty();               // preamble
        _upserted[1].SectionHeading.Should().Be("Setup");
        _upserted[2].SectionHeading.Should().Be("Setup > macOS");     // breadcrumb

        // Section prefix embedded in the chunk text gives the retriever context.
        _upserted[2].Content.Should().Contain("[Section: Setup > macOS]");
        _upserted[2].Content.Should().Contain("macOS-specific steps.");
    }

    [Fact]
    public async Task Ingest_WithoutFrontMatter_FallsBackToFileName()
    {
        await WriteDoc("onboarding.md", "# Welcome\n\nOnboarding checklist content.");

        await CreateService().IngestAllAsync();

        _upserted.Should().ContainSingle().Which.Title.Should().Be("onboarding");
    }

    [Fact]
    public async Task Ingest_SplitsLongSections_WithOverlap()
    {
        var paragraphs = string.Join("\n\n",
            Enumerable.Range(0, 6).Select(i => $"Paragraph {i} " + new string((char)('a' + i), 80)));
        await WriteDoc("long.md", "# Big Section\n\n" + paragraphs);

        await CreateService(chunkSize: 200, chunkOverlap: 30).IngestAllAsync();

        _upserted.Should().HaveCountGreaterThan(1);
        _upserted.Should().AllSatisfy(c => c.SectionHeading.Should().Be("Big Section"));

        // Consecutive chunks share overlap so no sentence is lost at a boundary.
        var tailOfFirst = _upserted[0].Content[^10..];
        _upserted[1].Content.Should().Contain(tailOfFirst);
    }

    [Fact]
    public async Task Reingest_WhenFileShrinks_DeletesTrailingStaleChunks()
    {
        var longBody = string.Join("\n\n",
            Enumerable.Range(0, 6).Select(i => $"Paragraph {i} " + new string('x', 80)));
        await WriteDoc("doc.md", "# Section\n\n" + longBody);

        await CreateService(chunkSize: 200, chunkOverlap: 20).IngestAllAsync();
        var originalCount = _upserted.Count;
        originalCount.Should().BeGreaterThan(1);

        _upserted.Clear();
        await WriteDoc("doc.md", "# Section\n\nJust one short paragraph now.");
        await CreateService(chunkSize: 200, chunkOverlap: 20).IngestAllAsync();

        _upserted.Should().HaveCount(1);
        _deletedKeys.Should().BeEquivalentTo(
            Enumerable.Range(1, originalCount - 1).Select(i => $"doc.md#{i}"));
    }

    [Fact]
    public async Task Reingest_WhenFileDeleted_RemovesAllItsChunks()
    {
        await WriteDoc("gone.md", "# Doc\n\nContent that will disappear.");
        await CreateService().IngestAllAsync();

        File.Delete(Path.Combine(_docsFolder, "gone.md"));
        await CreateService().IngestAllAsync();

        _deletedKeys.Should().Contain("gone.md#0");
        (await _fullText.SearchAsync("disappear", top: 5)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_PopulatesFullTextIndex()
    {
        await WriteDoc("search.md", "# Search\n\nThe flux capacitor requires 1.21 gigawatts.");

        await CreateService().IngestAllAsync();

        var hits = await _fullText.SearchAsync("flux capacitor", top: 5);
        hits.Should().Contain("search.md#0");
    }

    [Fact]
    public async Task Ingest_SkipsEmptyFiles()
    {
        await WriteDoc("empty.md", "   \n\n  ");

        await CreateService().IngestAllAsync();

        _upserted.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_ScansSubdirectories()
    {
        Directory.CreateDirectory(Path.Combine(_docsFolder, "nested"));
        await File.WriteAllTextAsync(
            Path.Combine(_docsFolder, "nested", "deep.md"), "# Deep\n\nNested content.");

        await CreateService().IngestAllAsync();

        _upserted.Should().ContainSingle().Which.Key.Should().Be("nested/deep.md#0");
    }
}
