using System.Collections.Concurrent;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;

namespace Nachos.Core.Tests.Worker;

/// <summary>
/// Passes every call to <paramref name="inner"/> and records the worker-facing queue and idempotency calls, so tests can
/// wait for a poll, a renewal or a reconciler tick instead of guessing how long one takes.
/// </summary>
internal sealed class ObservedStore(IMemoryStore inner) : IMemoryStore, IWorkQueue, IIdempotencyStore, IEmbeddingStore
{
    private int _renewals;
    private int _ticks;
    private int _leaseAttempts;
    private TaskCompletionSource? _hang;

    public IWorkspaceStore Workspaces => inner.Workspaces;
    public IPeerStore Peers => inner.Peers;
    public ISessionStore Sessions => inner.Sessions;
    public IMessageStore Messages => inner.Messages;
    public IGrantStore Grants => inner.Grants;
    public IIdempotencyStore Idempotency => this;
    public IConclusionStore Conclusions => inner.Conclusions;
    public IPeerCardStore PeerCards => inner.PeerCards;
    public IWorkQueue WorkQueue => this;
    public IEmbeddingStore Embeddings => this;

    /// <summary>A <see cref="TransientStoreException"/> is transient, as a store would report a deadlock.</summary>
    public bool IsTransientFailure(Exception exception) =>
        exception is TransientStoreException || inner.IsTransientFailure(exception);

    /// <summary>While set, <see cref="TryAcquireAsync"/> throws, as an unavailable store would.</summary>
    public volatile bool FailAcquire;

    /// <summary>While set, <see cref="TryLeaseAsync"/> throws, as an unavailable store would.</summary>
    public volatile bool FailLease;

    /// <summary>While set, each renewal throws what it returns instead of renewing.</summary>
    public volatile Func<WorkLease, Exception>? RenewalFailure;

    /// <summary>While set, <see cref="SaveAsync"/> saves nothing, as a save that did not take.</summary>
    public volatile bool DropEmbeddingSaves;

    /// <summary>Calls of <see cref="TryLeaseAsync"/>, failed ones included.</summary>
    public int LeaseAttempts => Volatile.Read(ref _leaseAttempts);

    /// <summary>The task types of every <see cref="TryAcquireAsync"/> call.</summary>
    public ConcurrentQueue<IReadOnlyList<WorkTaskType>> Acquires { get; } = new();

    public ConcurrentQueue<WorkLease> Failures { get; } = new();

    /// <summary>The retry delay of each <see cref="FailAsync"/> call, in order.</summary>
    public ConcurrentQueue<TimeSpan> RetryDelays { get; } = new();

    public ConcurrentQueue<WorkLease> Releases { get; } = new();

    /// <summary>The retry delay of each <see cref="ReleaseAsync"/> call, in order.</summary>
    public ConcurrentQueue<TimeSpan> ReleaseDelays { get; } = new();

    /// <summary>
    /// When set, the next renewal hangs until cancelled and then throws <see cref="InvalidOperationException"/>, as a
    /// provider that reports its own cancellation as a provider error would; set when it starts hanging.
    /// </summary>
    public TaskCompletionSource? HangNextRenewal { get => _hang; set => _hang = value; }

    public int Renewals => Volatile.Read(ref _renewals);

    /// <summary>Completed reconciler ticks: idempotency purges, a tick's last step.</summary>
    public int Ticks => Volatile.Read(ref _ticks);

    public Task<WorkLease?> TryAcquireAsync(
        string owner, IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, TimeSpan leaseDuration, CancellationToken ct)
    {
        Acquires.Enqueue([.. taskTypes.Keys.Order()]);
        return FailAcquire
            ? throw new InvalidOperationException("The store is unavailable.")
            : inner.WorkQueue.TryAcquireAsync(owner, taskTypes, leaseDuration, ct);
    }

    public Task<WorkLease?> TryLeaseAsync(string workUnitKey, string owner, TimeSpan leaseDuration, CancellationToken ct)
    {
        Interlocked.Increment(ref _leaseAttempts);
        return FailLease
            ? throw new InvalidOperationException("The store is unavailable.")
            : inner.WorkQueue.TryLeaseAsync(workUnitKey, owner, leaseDuration, ct);
    }

    public async Task RenewAsync(WorkLease lease, TimeSpan leaseDuration, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _hang, null) is { } hanging)
        {
            hanging.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Operation cancelled by user.");
            }
        }

        if (RenewalFailure is { } failure)
        {
            throw failure(lease);
        }

        await inner.WorkQueue.RenewAsync(lease, leaseDuration, ct);
        Interlocked.Increment(ref _renewals);
    }

    public Task CompleteAsync(WorkLease lease, CancellationToken ct) => inner.WorkQueue.CompleteAsync(lease, ct);

    public Task ReleaseAsync(WorkLease lease, TimeSpan retryDelay, CancellationToken ct)
    {
        Releases.Enqueue(lease);
        ReleaseDelays.Enqueue(retryDelay);
        return inner.WorkQueue.ReleaseAsync(lease, retryDelay, ct);
    }

    public Task FailAsync(WorkLease lease, string reason, TimeSpan retryDelay, int maxAttempts, CancellationToken ct)
    {
        Failures.Enqueue(lease);
        RetryDelays.Enqueue(retryDelay);
        return inner.WorkQueue.FailAsync(lease, reason, retryDelay, maxAttempts, ct);
    }

    public Task<QueueStatusRecord> GetStatusAsync(
        string workspaceName, string? observerName, string? senderName, string? sessionName, CancellationToken ct) =>
        inner.WorkQueue.GetStatusAsync(workspaceName, observerName, senderName, sessionName, ct);

    /// <summary>While set, <see cref="GetBacklogAsync"/> throws, as an unavailable store would.</summary>
    public volatile bool FailBacklog;

    public Task<QueueBacklog> GetBacklogAsync(IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, CancellationToken ct) =>
        FailBacklog
            ? throw new InvalidOperationException("The store is unavailable.")
            : inner.WorkQueue.GetBacklogAsync(taskTypes, ct);

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

    public Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(int limit, CancellationToken ct) =>
        inner.Embeddings.ListPendingAsync(limit, ct);

    public Task<long> CountPendingAsync(CancellationToken ct) => inner.Embeddings.CountPendingAsync(ct);

    public Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, string embeddingModel, CancellationToken ct) =>
        DropEmbeddingSaves ? Task.CompletedTask : inner.Embeddings.SaveAsync(pending, embedding, embeddingModel, ct);

    public Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct) => inner.Embeddings.MarkFailedAsync(pending, ct);
}

/// <summary>A store failure that <see cref="ObservedStore.IsTransientFailure"/> reports as transient.</summary>
internal sealed class TransientStoreException() : Exception("The database chose this transaction as a deadlock victim.");
