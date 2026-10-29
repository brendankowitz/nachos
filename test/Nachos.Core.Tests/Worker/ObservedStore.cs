using System.Collections.Concurrent;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;

namespace Nachos.Core.Tests.Worker;

/// <summary>
/// Passes every call to <paramref name="inner"/> and records the worker-facing queue and idempotency calls, so tests can
/// wait for a poll, a renewal or a reconciler tick instead of guessing how long one takes.
/// </summary>
internal sealed class ObservedStore(IMemoryStore inner) : IMemoryStore, IWorkQueue, IIdempotencyStore
{
    private int _renewals;
    private int _ticks;

    public IWorkspaceStore Workspaces => inner.Workspaces;
    public IPeerStore Peers => inner.Peers;
    public ISessionStore Sessions => inner.Sessions;
    public IMessageStore Messages => inner.Messages;
    public IGrantStore Grants => inner.Grants;
    public IIdempotencyStore Idempotency => this;
    public IConclusionStore Conclusions => inner.Conclusions;
    public IPeerCardStore PeerCards => inner.PeerCards;
    public IWorkQueue WorkQueue => this;
    public IEmbeddingStore Embeddings => inner.Embeddings;

    /// <summary>The task types of every <see cref="TryAcquireAsync"/> call.</summary>
    public ConcurrentQueue<IReadOnlyList<WorkTaskType>> Acquires { get; } = new();

    public ConcurrentQueue<WorkLease> Failures { get; } = new();

    public int Renewals => Volatile.Read(ref _renewals);

    /// <summary>Completed reconciler ticks: idempotency purges, a tick's last step.</summary>
    public int Ticks => Volatile.Read(ref _ticks);

    public Task<WorkLease?> TryAcquireAsync(
        string owner, IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, TimeSpan leaseDuration, CancellationToken ct)
    {
        Acquires.Enqueue([.. taskTypes.Keys.Order()]);
        return inner.WorkQueue.TryAcquireAsync(owner, taskTypes, leaseDuration, ct);
    }

    public Task<WorkLease?> TryLeaseAsync(string workUnitKey, string owner, TimeSpan leaseDuration, CancellationToken ct) =>
        inner.WorkQueue.TryLeaseAsync(workUnitKey, owner, leaseDuration, ct);

    public async Task RenewAsync(WorkLease lease, TimeSpan leaseDuration, CancellationToken ct)
    {
        await inner.WorkQueue.RenewAsync(lease, leaseDuration, ct);
        Interlocked.Increment(ref _renewals);
    }

    public Task CompleteAsync(WorkLease lease, CancellationToken ct) => inner.WorkQueue.CompleteAsync(lease, ct);

    public Task FailAsync(WorkLease lease, string reason, TimeSpan retryDelay, int maxAttempts, CancellationToken ct)
    {
        Failures.Enqueue(lease);
        return inner.WorkQueue.FailAsync(lease, reason, retryDelay, maxAttempts, ct);
    }

    public Task<QueueStatusRecord> GetStatusAsync(
        string workspaceName, string? observerName, string? senderName, string? sessionName, CancellationToken ct) =>
        inner.WorkQueue.GetStatusAsync(workspaceName, observerName, senderName, sessionName, ct);

    public Task<int> PruneSucceededAsync(TimeSpan retention, CancellationToken ct) =>
        inner.WorkQueue.PruneSucceededAsync(retention, ct);

    public Task<IdempotencyRecord?> TryGetAsync(string workspaceName, string key, CancellationToken ct) =>
        inner.Idempotency.TryGetAsync(workspaceName, key, ct);

    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var purged = await inner.Idempotency.PurgeExpiredAsync(ct);
        Interlocked.Increment(ref _ticks);
        return purged;
    }
}
