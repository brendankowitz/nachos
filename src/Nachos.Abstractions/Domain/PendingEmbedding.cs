namespace Nachos.Abstractions.Domain;

/// <summary>Content waiting for its embedding, as it was when it was claimed.</summary>
/// <param name="SourceId">The conclusion's public id, or the chunk's message public id.</param>
/// <param name="ChunkIndex">The chunk's position in its message; 0 for a conclusion.</param>
public sealed record PendingEmbedding(
    PendingEmbeddingKind Kind, string WorkspaceName, string SourceId, int ChunkIndex, string Content)
{
    public static PendingEmbedding ForConclusion(string workspaceName, string conclusionId, string content) =>
        new(PendingEmbeddingKind.Conclusion, workspaceName, conclusionId, 0, content);

    public static PendingEmbedding ForChunk(string workspaceName, string messageId, int chunkIndex, string content) =>
        new(PendingEmbeddingKind.MessageChunk, workspaceName, messageId, chunkIndex, content);
}
