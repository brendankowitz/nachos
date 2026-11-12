namespace Nachos.Abstractions.Domain;

/// <summary>Where a job's work item stands; the same buckets as <see cref="QueueCounts"/>.</summary>
public enum JobStatus
{
    /// <summary>Waiting to be claimed, including after a failed attempt that will be retried.</summary>
    Pending,

    /// <summary>Its unit holds an unexpired lease.</summary>
    InProgress,

    /// <summary>Succeeded, or superseded (see <see cref="JobRecord.Error"/>).</summary>
    Completed,

    /// <summary>Failed its last allowed attempt; the resource stays <see cref="LifecycleState.Deleting"/>.</summary>
    DeadLettered,
}
