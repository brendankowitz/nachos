using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

/// <summary>The work queue over <see cref="WorkQueueState"/>; every call takes its gate for one short critical section.</summary>
internal sealed class InMemoryWorkQueue(InMemoryState state) : IWorkQueue
{
    public Task<WorkLease?> TryAcquireAsync(
        string owner,
        IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes,
        TimeSpan leaseDuration,
        CancellationToken ct) =>
        state.Run(
            () =>
            {
                RequireOwner(owner);
                ArgumentNullException.ThrowIfNull(taskTypes);
                lock (state.Queue.Gate)
                {
                    return state.Queue.TryAcquire(owner, taskTypes, leaseDuration, state.Clock.GetUtcNow());
                }
            },
            ct);

    public Task<WorkLease?> TryLeaseAsync(string workUnitKey, string owner, TimeSpan leaseDuration, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(workUnitKey);
                if (workUnitKey.Length > WorkUnitKeys.MaxLength)
                {
                    throw new ArgumentException(
                        $"A work-unit key must be at most {WorkUnitKeys.MaxLength} characters; this one has {workUnitKey.Length}.",
                        nameof(workUnitKey));
                }

                RequireOwner(owner);
                lock (state.Queue.Gate)
                {
                    return state.Queue.TryLease(workUnitKey, owner, leaseDuration, state.Clock.GetUtcNow());
                }
            },
            ct);

    public Task RenewAsync(WorkLease lease, TimeSpan leaseDuration, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(lease);
                lock (state.Queue.Gate)
                {
                    state.Queue.Renew(lease, leaseDuration, state.Clock.GetUtcNow());
                }
            },
            ct);

    public Task CompleteAsync(WorkLease lease, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(lease);
                lock (state.Queue.Gate)
                {
                    state.Queue.Complete(lease, state.Clock.GetUtcNow());
                }
            },
            ct);

    public Task ReleaseAsync(WorkLease lease, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(lease);
                lock (state.Queue.Gate)
                {
                    state.Queue.GiveBack(lease);
                }
            },
            ct);

    public Task FailAsync(WorkLease lease, string reason, TimeSpan retryDelay, int maxAttempts, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(lease);
                ArgumentNullException.ThrowIfNull(reason);
                ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
                lock (state.Queue.Gate)
                {
                    state.Queue.Fail(lease, reason, retryDelay, maxAttempts, state.Clock.GetUtcNow());
                }
            },
            ct);

    public Task<QueueStatusRecord> GetStatusAsync(
        string workspaceName, string? observerName, string? senderName, string? sessionName, CancellationToken ct) =>
        state.Run(
            () =>
            {
                state.RequireWorkspace(workspaceName);
                lock (state.Queue.Gate)
                {
                    return state.Queue.Status(workspaceName, observerName, senderName, sessionName, state.Clock.GetUtcNow());
                }
            },
            ct);

    /// <summary>The bound of the SQL <c>Owner</c> column, so both providers reject the same owners.</summary>
    private static void RequireOwner(string owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Length > WorkLease.MaxOwnerLength)
        {
            throw new ArgumentException(
                $"An owner must be at most {WorkLease.MaxOwnerLength} characters; this one has {owner.Length}.", nameof(owner));
        }
    }

    public Task<int> PruneSucceededAsync(TimeSpan retention, CancellationToken ct) =>
        state.Run(
            () =>
            {
                lock (state.Queue.Gate)
                {
                    return state.Queue.PruneSucceeded(retention, state.Clock.GetUtcNow());
                }
            },
            ct);
}
