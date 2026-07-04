using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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

public sealed class WebhookSignatureTests : IDisposable
{
    private const string Payload = """{"eventType":"git.push","resource":{}}""";
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private GitHubIngestionService GitHubService(string secret) =>
        new(Substitute.For<IHttpClientFactory>(),
            Options.Create(new GitHubOptions { WebhookSecret = secret }),
            NullLogger<GitHubIngestionService>.Instance);

    private AzureDevOpsIngestionService AdoService(string secret)
    {
        var ragOptions = Options.Create(new RagOptions
        {
            DocsFolder = _dir.Path,
            VectorDbPath = _dir.File("test.db"),
        });
        var ingestion = new MarkdownIngestionService(
            Stubs.Embedder(),
            Substitute.For<VectorStoreCollection<string, DocumentChunk>>(),
            new FullTextIndex($"Data Source={_dir.File("test.db")}"),
            ragOptions,
            NullLogger<MarkdownIngestionService>.Instance);
        var synchronizer = new DocumentSourceSynchronizer(
            ingestion, ragOptions, NullLogger<DocumentSourceSynchronizer>.Instance);

        return new AzureDevOpsIngestionService(
            Substitute.For<IHttpClientFactory>(),
            synchronizer,
            Options.Create(new AzureDevOpsOptions { WebhookSecret = secret }),
            NullLogger<AzureDevOpsIngestionService>.Instance);
    }

    private static string Hmac(string payload, string secret, Func<byte[], HMAC> factory)
    {
        using var hmac = factory(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    // ── GitHub (HMAC-SHA256, X-Hub-Signature-256) ────────────────────────────────

    [Fact]
    public void GitHub_ValidSignature_IsAccepted()
    {
        var sig = "sha256=" + Hmac(Payload, "s3cret", key => new HMACSHA256(key));

        GitHubService("s3cret").ValidateWebhookSignature(Payload, sig).Should().BeTrue();
    }

    [Fact]
    public void GitHub_WrongSecret_IsRejected()
    {
        var sig = "sha256=" + Hmac(Payload, "wrong", key => new HMACSHA256(key));

        GitHubService("s3cret").ValidateWebhookSignature(Payload, sig).Should().BeFalse();
    }

    [Fact]
    public void GitHub_MissingPrefix_IsRejected()
    {
        var sig = Hmac(Payload, "s3cret", key => new HMACSHA256(key));

        GitHubService("s3cret").ValidateWebhookSignature(Payload, sig).Should().BeFalse();
    }

    [Fact]
    public void GitHub_NoSecretConfigured_AcceptsAnything()
    {
        GitHubService("").ValidateWebhookSignature(Payload, "").Should().BeTrue();
    }

    // ── Azure DevOps (HMAC-SHA1, X-Hub-Signature) ────────────────────────────────

    [Fact]
    public void Ado_ValidSignature_IsAccepted()
    {
        var sig = "sha1=" + Hmac(Payload, "s3cret", key => new HMACSHA1(key));

        AdoService("s3cret").ValidateWebhookSignature(Payload, sig).Should().BeTrue();
    }

    [Fact]
    public void Ado_TamperedPayload_IsRejected()
    {
        var sig = "sha1=" + Hmac(Payload, "s3cret", key => new HMACSHA1(key));

        AdoService("s3cret").ValidateWebhookSignature(Payload + "x", sig).Should().BeFalse();
    }

    [Fact]
    public void Ado_NoSecretConfigured_AcceptsAnything()
    {
        AdoService("").ValidateWebhookSignature(Payload, "").Should().BeTrue();
    }
}
