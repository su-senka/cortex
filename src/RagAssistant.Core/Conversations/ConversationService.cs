using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RagAssistant.Core.Conversations;

public sealed class ConversationService(string connectionString, ILogger<ConversationService> logger)
{
    public async Task EnsureTablesAsync(CancellationToken ct = default)
    {
        using var conn = Open();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS conversations (
                id          TEXT PRIMARY KEY,
                user_id     TEXT NOT NULL,
                title       TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id              TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                role            TEXT NOT NULL,
                content         TEXT NOT NULL,
                created_at      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS feedback (
                message_id  TEXT PRIMARY KEY,
                rating      INTEGER NOT NULL,
                created_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_conv_user   ON conversations(user_id);
            CREATE INDEX IF NOT EXISTS idx_msg_conv_id ON messages(conversation_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogDebug("Conversation tables ready.");
    }

    public async Task<Conversation> CreateConversationAsync(
        string userId, string title, CancellationToken ct = default)
    {
        var conv = new Conversation(
            Guid.NewGuid().ToString("N"),
            userId,
            title.Length > 120 ? title[..120] : title,
            DateTimeOffset.UtcNow);

        using var conn = Open();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO conversations (id, user_id, title, created_at) VALUES ($id, $uid, $title, $at)";
        cmd.Parameters.AddWithValue("$id",    conv.Id);
        cmd.Parameters.AddWithValue("$uid",   conv.UserId);
        cmd.Parameters.AddWithValue("$title", conv.Title);
        cmd.Parameters.AddWithValue("$at",    conv.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return conv;
    }

    public async Task<IReadOnlyList<Conversation>> ListConversationsAsync(
        string userId, CancellationToken ct = default)
    {
        using var conn = Open();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, user_id, title, created_at FROM conversations WHERE user_id = $uid ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$uid", userId);

        var result = new List<Conversation>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new Conversation(
                reader.GetString(0), reader.GetString(1),
                reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3))));
        return result;
    }

    public async Task<bool> BelongsToUserAsync(
        string userId, string conversationId, CancellationToken ct = default)
    {
        using var conn = Open();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM conversations WHERE id = $cid AND user_id = $uid LIMIT 1";
        cmd.Parameters.AddWithValue("$cid", conversationId);
        cmd.Parameters.AddWithValue("$uid", userId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task DeleteConversationAsync(
        string userId, string conversationId, CancellationToken ct = default)
    {
        using var conn = Open();
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();

        using var delMsg = conn.CreateCommand();
        delMsg.Transaction = tx;
        delMsg.CommandText = """
            DELETE FROM messages WHERE conversation_id IN
                (SELECT id FROM conversations WHERE id = $cid AND user_id = $uid)
            """;
        delMsg.Parameters.AddWithValue("$cid", conversationId);
        delMsg.Parameters.AddWithValue("$uid", userId);
        await delMsg.ExecuteNonQueryAsync(ct);

        using var delConv = conn.CreateCommand();
        delConv.Transaction = tx;
        delConv.CommandText = "DELETE FROM conversations WHERE id = $cid AND user_id = $uid";
        delConv.Parameters.AddWithValue("$cid", conversationId);
        delConv.Parameters.AddWithValue("$uid", userId);
        await delConv.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string conversationId, CancellationToken ct = default)
    {
        using var conn = Open();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, conversation_id, role, content, created_at FROM messages WHERE conversation_id = $cid ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("$cid", conversationId);

        var result = new List<ConversationMessage>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ConversationMessage(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        return result;
    }

    /// <summary>
    /// Saves a user message and the assistant reply in one transaction.
    /// Returns the assistant message ID so the caller can emit it to the client for feedback linking.
    /// </summary>
    public async Task<string> SaveExchangeAsync(
        string conversationId, string userContent, string assistantContent,
        CancellationToken ct = default)
    {
        var now             = DateTimeOffset.UtcNow;
        var assistantMsgId  = Guid.NewGuid().ToString("N");

        using var conn = Open();
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();

        await InsertMessageAsync(conn, tx, conversationId, "user",      Guid.NewGuid().ToString("N"), userContent,      now,                    ct);
        await InsertMessageAsync(conn, tx, conversationId, "assistant", assistantMsgId,               assistantContent, now.AddMilliseconds(1), ct);

        await tx.CommitAsync(ct);
        return assistantMsgId;
    }

    public async Task SaveFeedbackAsync(string messageId, int rating, CancellationToken ct = default)
    {
        using var conn = Open();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        // REPLACE handles the case where the user changes their vote.
        cmd.CommandText =
            "INSERT OR REPLACE INTO feedback (message_id, rating, created_at) VALUES ($mid, $rating, $at)";
        cmd.Parameters.AddWithValue("$mid",    messageId);
        cmd.Parameters.AddWithValue("$rating", rating);
        cmd.Parameters.AddWithValue("$at",     DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertMessageAsync(
        SqliteConnection conn, SqliteTransaction tx,
        string conversationId, string role, string id, string content,
        DateTimeOffset at, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO messages (id, conversation_id, role, content, created_at) VALUES ($id, $cid, $role, $content, $at)";
        cmd.Parameters.AddWithValue("$id",      id);
        cmd.Parameters.AddWithValue("$cid",     conversationId);
        cmd.Parameters.AddWithValue("$role",    role);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$at",      at.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection Open() => new(connectionString);
}
