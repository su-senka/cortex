namespace RagAssistant.Core.Models;

public sealed class RagQueryResult
{
    /// <summary>Retrieved source chunks, determined before the LLM call.</summary>
    public required IReadOnlyList<SourceReference> Sources { get; init; }

    /// <summary>Streams LLM output tokens as they arrive.</summary>
    public required IAsyncEnumerable<string> AnswerStream { get; init; }
}
