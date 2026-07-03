using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RagAssistant.Core;

/// <summary>
/// BM25 keyword index over chunk content, backed by SQLite FTS5 in the same
/// database file as the vector table. Provides the lexical half of hybrid search;
/// kept in sync with the vector store by <c>MarkdownIngestionService</c>.
/// </summary>
public sealed class FullTextIndex(string connectionString)
{
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        // key is UNINDEXED — it's an identifier, not searchable text.
        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                key UNINDEXED,
                content,
                tokenize = 'porter unicode61'
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(
        IReadOnlyList<(string Key, string Content)> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();

        foreach (var (key, content) in chunks)
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM chunks_fts WHERE key = $key";
            del.Parameters.AddWithValue("$key", key);
            await del.ExecuteNonQueryAsync(ct);

            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO chunks_fts (key, content) VALUES ($key, $content)";
            ins.Parameters.AddWithValue("$key", key);
            ins.Parameters.AddWithValue("$content", content);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task DeleteAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();

        foreach (var key in keys)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM chunks_fts WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Runs a BM25 search and returns chunk keys, best match first.
    /// The raw question is reduced to bare word tokens joined with OR so user
    /// punctuation can never produce invalid FTS5 query syntax.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchAsync(
        string query, int top, CancellationToken ct = default)
    {
        var tokens = TokenRegex.Matches(query)
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Count == 0) return [];

        var match = string.Join(" OR ", tokens.Select(t => $"\"{t}\""));

        using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        // bm25() is a rank where lower = more relevant.
        cmd.CommandText = """
            SELECT key FROM chunks_fts
            WHERE chunks_fts MATCH $match
            ORDER BY bm25(chunks_fts)
            LIMIT $top
            """;
        cmd.Parameters.AddWithValue("$match", match);
        cmd.Parameters.AddWithValue("$top", top);

        var keys = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(reader.GetString(0));
        return keys;
    }
}
