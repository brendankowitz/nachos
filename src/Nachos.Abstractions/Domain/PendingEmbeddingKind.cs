namespace Nachos.Abstractions.Domain;

/// <summary>What a <see cref="PendingEmbedding"/> embeds.</summary>
public enum PendingEmbeddingKind
{
    /// <summary>A conclusion's content.</summary>
    Conclusion = 1,

    /// <summary>One chunk of a message's content.</summary>
    MessageChunk = 2,
}
