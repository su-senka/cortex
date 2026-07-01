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
