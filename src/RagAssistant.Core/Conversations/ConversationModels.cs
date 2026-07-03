namespace RagAssistant.Core.Conversations;

public sealed record Conversation(
    string Id,
    string UserId,
    string Title,
    DateTimeOffset CreatedAt);

public sealed record ConversationMessage(
    string Id,
    string ConversationId,
    string Role,   // "user" or "assistant"
    string Content,
    DateTimeOffset CreatedAt);

public sealed record RatedExchange(
    int Rating,
    DateTimeOffset RatedAt,
    string Question,
    string AnswerSnippet);

public sealed record DailyVolume(string Day, int Count);

public sealed record FeedbackAudit(
    int ThumbsUp,
    int ThumbsDown,
    IReadOnlyList<RatedExchange> RecentExchanges,
    IReadOnlyList<DailyVolume> DailyVolume);
