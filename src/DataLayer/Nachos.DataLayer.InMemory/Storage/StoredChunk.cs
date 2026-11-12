namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>
/// One embedding chunk of a stored message. Immutable and replaced on every change; guarded by its workspace's gate.
/// </summary>
/// <param name="Embedding">Set exactly when <paramref name="State"/> is <see cref="EmbeddingState.Synced"/>; store-owned.</param>
/// <param name="Order">The insertion number, the tiebreak for equal <c>CreatedAt</c> in the embedding backlog.</param>
internal sealed record StoredChunk(
    string MessageId,
    int ChunkIndex,
    string Content,
    float[]? Embedding,
    EmbeddingState State,
    DateTimeOffset CreatedAt,
    long Order)
{
    public static StoredChunk Pending(string messageId, int chunkIndex, string content, DateTimeOffset now, long order) =>
        new(messageId, chunkIndex, content, null, EmbeddingState.Pending, now, order);

    public bool IsPendingWith(string content) =>
        State == EmbeddingState.Pending && string.Equals(Content, content, StringComparison.Ordinal);
}
