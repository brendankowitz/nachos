using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// Queued deletion of sessions and workspaces (spec §13.1): acceptance behind the write barrier, and the resumable
/// purge run by the <see cref="WorkTaskType.Deletion"/> handler. See <see cref="IMemoryStore"/> for the shared rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Write barrier.</b> Once a workspace or session is <see cref="LifecycleState.Deleting"/>, every ordinary write under
/// it throws <see cref="ConflictException"/> (<c>"workspace is being deleted"</c> or <c>"session is being deleted"</c>),
/// and worker commits under it are discarded, until the deletion job removes the row. Only the members below that take a
/// lease may write under a deleting resource.
/// </para>
/// <para>
/// <b>Job fencing.</b> <see cref="CancelOtherWorkAsync"/>, <see cref="PurgeBatchAsync"/> and <see cref="RemoveAsync"/>
/// each first check that their lease is still held and that one of its items is the target's deletion item
/// (the one acceptance recorded); otherwise they throw <see cref="LeaseLostException"/> with nothing written.
/// </para>
/// </remarks>
public interface IDeletionStore
{
    /// <summary>
    /// Accepts a session's deletion: marks it <see cref="LifecycleState.Deleting"/> and enqueues its
    /// <see cref="WorkTaskType.Deletion"/> item (key <see cref="DeletionTarget.WorkUnitKey"/>, payload
    /// <see cref="Work.DeletionPayload"/>), in one transaction. A session already being deleted is left as it is, and its
    /// existing job is returned.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    /// <exception cref="ConflictException">The workspace is being deleted.</exception>
    Task<DeletionAcceptance> BeginDeleteSessionAsync(string workspaceName, string sessionName, CancellationToken ct);

    /// <summary>
    /// Accepts a workspace's deletion as <see cref="BeginDeleteSessionAsync"/> does a session's. A workspace already being
    /// deleted returns its existing job, whatever its sessions.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    /// <exception cref="ConflictException">The workspace has an <see cref="LifecycleState.Active"/> session.</exception>
    Task<DeletionAcceptance> BeginDeleteWorkspaceAsync(string workspaceName, CancellationToken ct);

    /// <summary>
    /// The deletion job's first step: removes the target's open work items other than deletions; for a workspace, marks the
    /// open deletion items of its sessions succeeded with the error <c>superseded_by_workspace_deletion</c>; and fences
    /// every other lease on the target's work units, so in-flight handlers fail their next lease check. The lease's own
    /// unit and items are untouched.
    /// </summary>
    /// <exception cref="LeaseLostException">The lease is no longer held, or is not the target's deletion lease.</exception>
    Task CancelOtherWorkAsync(WorkLease lease, DeletionTarget target, CancellationToken ct);

    /// <summary>
    /// Deletes at most <paramref name="batchSize"/> of the target's remaining rows, children first, and checkpoints the
    /// progress, so a job that loses its lease resumes where it stopped. A session's rows go in this order: message
    /// chunks, summaries, its explicit conclusions (then each conclusion citing one of those is tombstoned when none of
    /// its sources is still live, present and not tombstoned), its work items other than its deletion, memberships,
    /// messages. A workspace's rows go: each session's rows, then conclusions, collections, peer cards, idempotency
    /// records, its grants, peers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tombstoning must survive a lost lease between removing a conclusion and re-evaluating its dependents. A provider
    /// may tombstone the dependents of each batch's removed conclusions in the same transaction, which needs no stored id
    /// list, or keep a durable checkpoint of the removed ids; either meets the contract.
    /// </para>
    /// <para>
    /// Tombstoning does not cascade: a conclusion citing one that this job only tombstoned (not removed) is not
    /// re-evaluated.
    /// </para>
    /// </remarks>
    /// <returns>True while rows remain; false once only the target's own row is left.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is less than 1.</exception>
    /// <exception cref="LeaseLostException">The lease is no longer held, or is not the target's deletion lease.</exception>
    Task<bool> PurgeBatchAsync(WorkLease lease, DeletionTarget target, int batchSize, CancellationToken ct);

    /// <summary>
    /// The job's last step: removes the target's row (and anything left under it) and completes the lease, in one
    /// transaction. The name is free for reuse afterwards.
    /// </summary>
    /// <exception cref="LeaseLostException">The lease is no longer held, or is not the target's deletion lease.</exception>
    Task RemoveAsync(WorkLease lease, DeletionTarget target, CancellationToken ct);

    /// <summary>The workspace's jobs (its deletions and its sessions'), in creation order (reversed when <see cref="PageRequest.Reverse"/>).</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<Page<JobRecord>> ListJobsAsync(string workspaceName, PageRequest page, CancellationToken ct);
}
