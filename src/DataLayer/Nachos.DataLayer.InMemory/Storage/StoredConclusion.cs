using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>Where a conclusion's embedding stands.</summary>
internal enum EmbeddingState
{
    /// <summary>Waiting for the reconciler; invisible to vector reads.</summary>
    Pending,

    /// <summary><see cref="StoredConclusion.Embedding"/> is set and matches the content.</summary>
    Synced,

    /// <summary>The reconciler gave up; invisible to vector reads.</summary>
    Failed,
}

/// <summary>
/// A stored conclusion, live or tombstoned. Immutable and replaced on every change; guarded by its workspace's gate.
/// Its JSON is store-owned: never handed out without a clone.
/// </summary>
/// <param name="Embedding">Set exactly when <paramref name="State"/> is <see cref="EmbeddingState.Synced"/>; store-owned.</param>
/// <param name="Order">The insertion number, the tiebreak for equal <c>CreatedAt</c>.</param>
internal sealed record StoredConclusion(
    ConclusionRecord Record,
    float[]? Embedding,
    EmbeddingState State,
    DateTimeOffset? DeletedAt,
    long Order)
{
    public bool IsLive => DeletedAt is null;

    public bool IsSearchable => IsLive && State == EmbeddingState.Synced;

    public bool InCollection(string observerName, string observedName) =>
        string.Equals(Record.ObserverName, observerName, StringComparison.Ordinal)
        && string.Equals(Record.ObservedName, observedName, StringComparison.Ordinal);

    public bool InSession(string? restrictToSession) =>
        restrictToSession is null || string.Equals(Record.SessionName, restrictToSession, StringComparison.Ordinal);

    /// <summary>Replaces the content; the embedding becomes <paramref name="embedding"/>, or pending when it is null.</summary>
    public StoredConclusion WithContent(string content, float[]? embedding) =>
        this with
        {
            Record = Record with { Content = content },
            Embedding = embedding,
            State = embedding is null ? EmbeddingState.Pending : EmbeddingState.Synced,
        };

    /// <summary>True while the conclusion is live, pending, and still has the content that was claimed for embedding.</summary>
    public bool IsPendingWith(string content) =>
        IsLive && State == EmbeddingState.Pending && string.Equals(Record.Content, content, StringComparison.Ordinal);
}
