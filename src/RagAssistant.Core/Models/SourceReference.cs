namespace RagAssistant.Core.Models;

/// <summary>
/// A document chunk that contributed to an answer.
/// Populated from vector search — not from the model output — so citations are exact.
/// </summary>
public sealed record SourceReference(
    string SourceFile,
    string Title,
    string SectionHeading,
    double Score);
