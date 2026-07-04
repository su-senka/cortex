using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RagAssistant.Core.Conversations;
using RagAssistant.Tests.Support;

namespace RagAssistant.Tests;

public sealed class ConversationServiceTests : IAsyncLifetime, IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ConversationService _service;

    public ConversationServiceTests() =>
        _service = new ConversationService(
            $"Data Source={_dir.File("conv.db")}",
            NullLogger<ConversationService>.Instance);

    public Task InitializeAsync() => _service.EnsureTablesAsync();
    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Conversations_AreIsolatedPerUser()
    {
        await _service.CreateConversationAsync("alice", "Alice's question");
        await _service.CreateConversationAsync("bob", "Bob's question");

        var aliceConvs = await _service.ListConversationsAsync("alice");

        aliceConvs.Should().ContainSingle().Which.Title.Should().Be("Alice's question");
    }

    [Fact]
    public async Task CreateConversation_TruncatesLongTitles()
    {
        var longTitle = new string('x', 500);

        var conv = await _service.CreateConversationAsync("alice", longTitle);

        conv.Title.Should().HaveLength(120);
    }

    [Fact]
    public async Task BelongsToUser_ChecksOwnership()
    {
        var conv = await _service.CreateConversationAsync("alice", "Q");

        (await _service.BelongsToUserAsync("alice", conv.Id)).Should().BeTrue();
        (await _service.BelongsToUserAsync("bob", conv.Id)).Should().BeFalse();
        (await _service.BelongsToUserAsync("alice", "nonexistent")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveExchange_PersistsMessagesInOrder()
    {
        var conv = await _service.CreateConversationAsync("alice", "Q");

        await _service.SaveExchangeAsync(conv.Id, "First question", "First answer");
        // Messages are ordered by timestamp and the assistant message is stored at
        // +1ms — space the exchanges out like real traffic so ordering is stable.
        await Task.Delay(10);
        await _service.SaveExchangeAsync(conv.Id, "Second question", "Second answer");

        var messages = await _service.GetMessagesAsync(conv.Id);

        messages.Should().HaveCount(4);
        messages.Select(m => m.Role).Should().ContainInOrder("user", "assistant", "user", "assistant");
        messages[0].Content.Should().Be("First question");
        messages[3].Content.Should().Be("Second answer");
    }

    [Fact]
    public async Task DeleteConversation_RemovesConversationAndMessages()
    {
        var conv = await _service.CreateConversationAsync("alice", "Q");
        await _service.SaveExchangeAsync(conv.Id, "Question", "Answer");

        await _service.DeleteConversationAsync("alice", conv.Id);

        (await _service.ListConversationsAsync("alice")).Should().BeEmpty();
        (await _service.GetMessagesAsync(conv.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteConversation_IgnoresForeignUser()
    {
        var conv = await _service.CreateConversationAsync("alice", "Q");
        await _service.SaveExchangeAsync(conv.Id, "Question", "Answer");

        await _service.DeleteConversationAsync("bob", conv.Id);

        (await _service.ListConversationsAsync("alice")).Should().HaveCount(1);
        (await _service.GetMessagesAsync(conv.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Feedback_IsAggregatedInAudit()
    {
        var conv = await _service.CreateConversationAsync("alice", "Q");
        var goodMsgId = await _service.SaveExchangeAsync(conv.Id, "Q1", "A1");
        var badMsgId  = await _service.SaveExchangeAsync(conv.Id, "Q2", "A2");

        await _service.SaveFeedbackAsync(goodMsgId, 1);
        await _service.SaveFeedbackAsync(badMsgId, -1);

        var audit = await _service.GetFeedbackAuditAsync();

        audit.ThumbsUp.Should().Be(1);
        audit.ThumbsDown.Should().Be(1);
        audit.RecentExchanges.Should().HaveCount(2);
    }

    [Fact]
    public async Task Feedback_ChangedVote_ReplacesPrevious()
    {
        var conv = await _service.CreateConversationAsync("alice", "Q");
        var msgId = await _service.SaveExchangeAsync(conv.Id, "Q1", "A1");

        await _service.SaveFeedbackAsync(msgId, 1);
        await _service.SaveFeedbackAsync(msgId, -1);

        var audit = await _service.GetFeedbackAuditAsync();

        audit.ThumbsUp.Should().Be(0);
        audit.ThumbsDown.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentWrites_DoNotThrow()
    {
        var conv = await _service.CreateConversationAsync("alice", "Q");

        // WAL mode + pooling should absorb concurrent writers without "database is locked".
        var tasks = Enumerable.Range(0, 20)
            .Select(i => _service.SaveExchangeAsync(conv.Id, $"Q{i}", $"A{i}"));
        await Task.WhenAll(tasks);

        (await _service.GetMessagesAsync(conv.Id)).Should().HaveCount(40);
    }
}
