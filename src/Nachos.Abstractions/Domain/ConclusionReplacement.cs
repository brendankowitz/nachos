namespace Nachos.Abstractions.Domain;

/// <summary>
/// Replaces a conclusion's content with a richer statement of the same fact. A replacement is a re-derivation, so it adds
/// one to <c>TimesDerived</c>, unless the id is also listed in <see cref="DerivationChanges.Corroborations"/>: then only
/// the corroborations count, so a conclusion in both is not counted twice.
/// </summary>
/// <param name="Embedding">
/// The new content's embedding; null makes the conclusion pending again. The conclusion's
/// <see cref="ConclusionRecord.InternalMetadata"/>, including its <see cref="VectorSpace.ModelMetadataKey"/>, is kept
/// unchanged; a database uses one embedding model (switching models needs a new database), so it still names the model.
/// </param>
public sealed record ConclusionReplacement(string ConclusionId, string Content, ReadOnlyMemory<float>? Embedding);
