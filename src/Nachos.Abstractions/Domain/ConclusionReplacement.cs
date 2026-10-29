namespace Nachos.Abstractions.Domain;

/// <summary>
/// Replaces a conclusion's content with a richer statement of the same fact. <c>TimesDerived</c> is unchanged; list the
/// id in <see cref="DerivationChanges.Corroborations"/> as well to count the derivation.
/// </summary>
/// <param name="Embedding">The new content's embedding; null makes the conclusion pending again.</param>
public sealed record ConclusionReplacement(string ConclusionId, string Content, ReadOnlyMemory<float>? Embedding);
