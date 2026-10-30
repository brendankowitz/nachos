namespace Nachos.Abstractions.Domain;

/// <summary>The work queue's backlog across all workspaces (<see cref="Stores.IWorkQueue.GetBacklogAsync"/>).</summary>
/// <param name="EligibleUnits">Units that are due and not under an unexpired lease.</param>
/// <param name="OldestOpenItemCreatedAt">When the oldest unfinished item was enqueued; null when there is none.</param>
public sealed record QueueBacklog(long EligibleUnits, DateTimeOffset? OldestOpenItemCreatedAt);
