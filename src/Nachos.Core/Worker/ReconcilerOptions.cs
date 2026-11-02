namespace Nachos.Core.Worker;

/// <summary>Reconciler settings, bound from <c>Reconciler</c>.</summary>
public sealed class ReconcilerOptions
{
    /// <summary>The wait between ticks; default 30 seconds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest wait between ticks while they fail; the wait doubles per consecutive failing tick from
    /// <see cref="Interval"/>; default 10 minutes.
    /// </summary>
    public TimeSpan MaxInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the singleton lease lasts before another replica may take over; default 2 minutes.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Tombstoned conclusions older than this are deleted; default 24 hours.</summary>
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Succeeded work items completed longer ago than this are deleted; default 7 days.</summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How many pending conclusions are embedded per generator call; default 64.</summary>
    public int EmbeddingBatchSize { get; set; } = 64;

    /// <summary>
    /// Failed embedding attempts, one per tick, before a conclusion is marked failed and leaves the backlog; default 5.
    /// </summary>
    public int EmbeddingMaxAttempts { get; set; } = 5;

    /// <summary>
    /// Consecutive ticks in which embeddings failed and none succeeded from which each further one is logged as an error;
    /// default 20, about 10 minutes at the default <see cref="Interval"/>.
    /// </summary>
    public int FailedEmbeddingTicksBeforeError { get; set; } = 20;
}
