using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Domain;

/// <summary>
/// A conclusion to insert into the collection of <paramref name="ObserverName"/> about <paramref name="ObservedName"/>.
/// </summary>
/// <param name="SessionName">The session an explicit conclusion was stated in, or null.</param>
/// <param name="Embedding">
/// The content's embedding, exactly <see cref="VectorSpace.Dimensions"/> long; null stores the conclusion as pending, for
/// the reconciler to embed through <see cref="Stores.IEmbeddingStore"/>.
/// </param>
/// <param name="SourceIds">The public ids of the conclusions this one was reasoned from; empty for explicit ones.</param>
/// <param name="InternalMetadata">Provenance (see <see cref="ConclusionRecord.InternalMetadata"/>); null is stored as <c>{}</c>.</param>
public sealed record NewConclusion(
    string ObserverName,
    string ObservedName,
    string? SessionName,
    ConclusionLevel Level,
    string Content,
    ReadOnlyMemory<float>? Embedding,
    IReadOnlyList<string> SourceIds,
    JsonObject? InternalMetadata);
