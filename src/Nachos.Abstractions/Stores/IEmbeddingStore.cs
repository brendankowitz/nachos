using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// The embedding backlog of conclusions stored without one, across all workspaces. Its caller (the reconciler) runs
/// under a singleton lease, so listing claims nothing. See <see cref="IMemoryStore"/> for the shared rules.
/// </summary>
public interface IEmbeddingStore
{
    /// <summary>Up to <paramref name="limit"/> pending live conclusions, oldest first.</summary>
    Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(int limit, CancellationToken ct);

    /// <summary>How many live conclusions are pending, in every workspace.</summary>
    Task<long> CountPendingAsync(CancellationToken ct);

    /// <summary>
    /// Stores the embedding of <paramref name="pending"/>'s content, and records <paramref name="embeddingModel"/> as the
    /// conclusion's <see cref="ConclusionRecord.InternalMetadata"/> <see cref="VectorSpace.ModelMetadataKey"/>. A no-op when the conclusion is
    /// no longer pending with that content (it was tombstoned, replaced or embedded meanwhile), so a stale vector is never
    /// stored.
    /// </summary>
    /// <param name="embeddingModel">The model that produced the vector.</param>
    /// <exception cref="ArgumentException">The vector is not <see cref="VectorSpace.Dimensions"/> long.</exception>
    Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, string embeddingModel, CancellationToken ct);

    /// <summary>
    /// Gives up on embedding <paramref name="pending"/>: it leaves the backlog and stays invisible to vector reads. A no-op
    /// under the same conditions as <see cref="SaveAsync"/>; a later content replacement makes it pending again.
    /// </summary>
    Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct);
}
