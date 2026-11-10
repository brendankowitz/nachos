namespace Nachos.Core.Deletion;

/// <summary>Deletion-job settings, bound from <c>Deletion</c>.</summary>
public sealed class DeletionOptions
{
    /// <summary>
    /// The largest <see cref="BatchSize"/>: below SQL Server's lock escalation threshold (about 5,000 locks per statement)
    /// for the rows a batch counts. A batch also deletes each removed conclusion's own source rows, which it does not
    /// count, so a batch of conclusions with many sources each can still escalate.
    /// </summary>
    public const int MaxBatchSize = 4000;

    /// <summary>
    /// The most rows one purge transaction counts and deletes, from 1 to <see cref="MaxBatchSize"/>; default 500. A removed
    /// conclusion's own source rows go with it, uncounted.
    /// </summary>
    public int BatchSize { get; set; } = 500;
}
