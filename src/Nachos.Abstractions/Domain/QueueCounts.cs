namespace Nachos.Abstractions.Domain;

/// <summary>Work-item counts by state. Each work item (one message's derivation, for all its observers) counts once.</summary>
/// <param name="Pending">Items waiting to be claimed, including failed ones waiting for their retry.</param>
/// <param name="InProgress">Unfinished items of units that currently hold an unexpired lease.</param>
/// <param name="Completed">Items that succeeded and have not been pruned.</param>
/// <param name="DeadLettered">Items that failed their last allowed attempt.</param>
public sealed record QueueCounts(long Pending, long InProgress, long Completed, long DeadLettered)
{
    public static QueueCounts Empty { get; } = new(0, 0, 0, 0);

    public long Total => Pending + InProgress + Completed + DeadLettered;
}
