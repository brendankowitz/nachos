using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
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
                ArgumentNullException.ThrowIfNull(owner);
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
                ArgumentNullException.ThrowIfNull(owner);
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
