using System.Diagnostics.CodeAnalysis;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// The fenced work queue. See <see cref="IMemoryStore"/> for the rules shared by all stores. In M2 the only producer is
/// <see cref="IMessageStore.AppendAsync(string, string, IReadOnlyList{NewMessage}, IdempotencyWrite?, IReadOnlyList{DeriveIntent}?, CancellationToken)"/>,
/// which enqueues in the append's own transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Units and leases.</b> Items with the same <see cref="WorkItemRecord.WorkUnitKey"/> form a unit, processed
/// serially in <see cref="WorkItemRecord.Id"/> order. At most one lease exists per unit. A lease stays held until it is
/// completed or failed, or until it has expired (the store's clock is strictly past its expiry) <i>and</i> another
/// owner has taken the unit; an expired lease nobody has taken is still held. Each new lease on a unit gets a fencing token greater than every earlier one on it.
/// </para>
/// <para>
/// <b>Item states.</b> An item is open (pending, or failed and waiting for <c>retryDelay</c>), succeeded, or
/// dead-lettered. Only open items whose retry time has passed are claimable; a unit whose lowest open item is still
/// waiting is not claimable at all, which keeps the unit's order.
/// </para>
/// <para>
/// Every write under a lease (<see cref="RenewAsync"/>, <see cref="CompleteAsync"/>, <see cref="FailAsync"/>,
/// <see cref="IConclusionStore.CommitDerivationAsync"/>) first checks that the lease is still held, and throws
/// <see cref="LeaseLostException"/> with nothing written when it is not.
/// </para>
/// </remarks>
[SuppressMessage("Naming", "CA1711", Justification = "The work queue is the spec's name for this contract (spec §7.1).")]
public interface IWorkQueue
{
    /// <summary>
    /// Leases the due unit, among the task types given, whose lowest claimable item is oldest, skipping units held by an
    /// unexpired lease. Due-ness and the batch size follow the task type's <see cref="WorkBatchPolicy"/>. The returned
    /// items have their <see cref="WorkItemRecord.Attempts"/> incremented. Returns null when nothing is due.
    /// </summary>
    /// <param name="owner">The worker's identity, at most <see cref="WorkLease.MaxOwnerLength"/> characters.</param>
    /// <param name="taskTypes">The task types to claim, each with its batching policy.</param>
    /// <param name="leaseDuration">How long the lease lasts before another owner may take the unit.</param>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is too long.</exception>
    Task<WorkLease?> TryAcquireAsync(
        string owner,
        IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes,
        TimeSpan leaseDuration,
        CancellationToken ct);

    /// <summary>
    /// Leases the named unit without claiming any item (<see cref="WorkLease.Items"/> is empty), so one owner at a time
    /// can run a singleton duty such as the reconciler. Returns null while another owner holds an unexpired lease on it.
    /// </summary>
    /// <param name="workUnitKey">At most <see cref="Work.WorkUnitKeys.MaxLength"/> characters.</param>
    /// <param name="owner">The worker's identity, at most <see cref="WorkLease.MaxOwnerLength"/> characters.</param>
    /// <exception cref="ArgumentException"><paramref name="workUnitKey"/> or <paramref name="owner"/> is too long.</exception>
    Task<WorkLease?> TryLeaseAsync(string workUnitKey, string owner, TimeSpan leaseDuration, CancellationToken ct);

    /// <summary>Extends the lease to <paramref name="leaseDuration"/> from now.</summary>
    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    Task RenewAsync(WorkLease lease, TimeSpan leaseDuration, CancellationToken ct);

    /// <summary>Marks the lease's items succeeded and releases the unit.</summary>
    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    Task CompleteAsync(WorkLease lease, CancellationToken ct);

    /// <summary>
    /// Records <paramref name="reason"/> (truncated to 4000 characters) on the lease's items and releases the unit. An
    /// item whose <see cref="WorkItemRecord.Attempts"/> has reached <paramref name="maxAttempts"/> is dead-lettered;
    /// the others become claimable again <paramref name="retryDelay"/> from now.
    /// </summary>
    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    Task FailAsync(WorkLease lease, string reason, TimeSpan retryDelay, int maxAttempts, CancellationToken ct);

    /// <summary>
    /// Counts the workspace's work items, optionally only those whose observer snapshot includes
    /// <paramref name="observerName"/>, whose message <paramref name="senderName"/> sent, or that belong to
    /// <paramref name="sessionName"/>.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<QueueStatusRecord> GetStatusAsync(
        string workspaceName, string? observerName, string? senderName, string? sessionName, CancellationToken ct);

    /// <summary>Deletes succeeded items completed at least <paramref name="retention"/> ago, in every workspace.</summary>
    /// <returns>How many items were deleted.</returns>
    Task<int> PruneSucceededAsync(TimeSpan retention, CancellationToken ct);
}
