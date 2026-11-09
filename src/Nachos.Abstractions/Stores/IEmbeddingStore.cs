using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// The embedding backlog, across all workspaces: conclusions stored without an embedding and message chunks
/// (<see cref="PendingEmbeddingKind"/>). Its caller (the reconciler) runs under a singleton lease, so listing claims
/// nothing. See <see cref="IMemoryStore"/> for the shared rules.
/// </summary>
public interface IEmbeddingStore
{
    /// <summary>
    /// Up to <paramref name="limit"/> pending live conclusions and message chunks, oldest first. Chunks of a workspace or
    /// session being deleted are left out, so data about to be deleted is never embedded.
    /// </summary>
    Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(int limit, CancellationToken ct);

    /// <summary>How many of the items <see cref="ListPendingAsync"/> lists are pending, in every workspace.</summary>
    Task<long> CountPendingAsync(CancellationToken ct);

    /// <summary>
    /// How many of the items <see cref="CountPendingAsync(CancellationToken)"/> counts are of <paramref name="kind"/>, for
    /// the backlog gauges. A store that cannot count by kind keeps this default, which reports the total for every kind.
    /// </summary>
    Task<long> CountPendingAsync(PendingEmbeddingKind kind, CancellationToken ct) => CountPendingAsync(ct);

    /// <summary>
    /// Stores the embedding of <paramref name="pending"/>'s content. For a conclusion it also records
    /// <paramref name="embeddingModel"/> as the conclusion's <see cref="ConclusionRecord.InternalMetadata"/>
    /// <see cref="VectorSpace.ModelMetadataKey"/>; a chunk has no metadata. A no-op when the item is no longer pending with
    /// that content (it was tombstoned, replaced, purged or embedded meanwhile), so a stale vector is never stored.
    /// </summary>
    /// <param name="embeddingModel">The model that produced the vector.</param>
    /// <exception cref="ArgumentException">The vector is not <see cref="VectorSpace.Dimensions"/> long.</exception>
    Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, string embeddingModel, CancellationToken ct);

    /// <summary>
    /// Gives up on embedding <paramref name="pending"/>: it leaves the backlog and stays invisible to vector reads. A no-op
    /// under the same conditions as <see cref="SaveAsync"/>; a later content replacement of a conclusion makes it pending
    /// again.
    /// </summary>
    Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="limit"/> messages that have no chunk, oldest (<c>CreatedAt</c>) first, in every workspace: the
    /// messages appended before chunking existed, for the reconciler to backfill. Messages whose content is blank
    /// (<see cref="string.IsNullOrWhiteSpace"/>) have no chunks by definition and are never listed, nor are messages of a
    /// workspace or session that is being deleted.
    /// </summary>
    Task<IReadOnlyList<MessageRecord>> ListUnchunkedMessagesAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Stores the chunks as pending embeddings. Idempotent on (message, chunk index): a chunk that already exists is left
    /// as it is. A chunk whose message no longer exists, or whose workspace or session is being deleted, is skipped.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A chunk index is negative or greater than <see cref="MessageChunk.MaxChunkIndex"/>.
    /// </exception>
    Task AddChunksAsync(IReadOnlyList<MessageChunk> chunks, CancellationToken ct);
}
