namespace Nachos.Abstractions.Domain;

/// <summary>A background job of a workspace, as <c>GET W/jobs</c> lists it. In M3 only deletions are jobs.</summary>
/// <param name="Id">The job's work item id.</param>
/// <param name="Attempts">How many times the item has been leased.</param>
/// <param name="Error">
/// The last failure, or the reason a completed item did nothing (<c>superseded_by_workspace_deletion</c>); null otherwise.
/// </param>
public sealed record JobRecord(
    long Id,
    WorkTaskType Type,
    DeletionTarget Target,
    JobStatus Status,
    int Attempts,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
