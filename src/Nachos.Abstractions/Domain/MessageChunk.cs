namespace Nachos.Abstractions.Domain;

/// <summary>One embedding chunk of a stored message, for <see cref="Stores.IEmbeddingStore.AddChunksAsync"/>.</summary>
/// <param name="MessageId">The message's public id.</param>
/// <param name="ChunkIndex">The chunk's 0-based position in its message, at most <see cref="MaxChunkIndex"/>.</param>
public sealed record MessageChunk(string WorkspaceName, string SessionName, string MessageId, int ChunkIndex, string Content)
{
    /// <summary>The largest chunk index a store accepts (the SQL <c>ChunkIndex smallint</c> column).</summary>
    public const int MaxChunkIndex = short.MaxValue;
}
