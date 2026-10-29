namespace Nachos.Core.Deriver;

/// <summary>A validated explicit fact, ready to deduplicate and store.</summary>
/// <param name="Content">Normalized with <see cref="ConclusionDeduplicator.Normalize"/>.</param>
/// <param name="Embedding">The embedding of <paramref name="Content"/>.</param>
/// <param name="SourceMessageIds">The target peer's messages that state the fact, in batch order.</param>
public sealed record DerivedFact(string Content, ReadOnlyMemory<float> Embedding, IReadOnlyList<string> SourceMessageIds);
