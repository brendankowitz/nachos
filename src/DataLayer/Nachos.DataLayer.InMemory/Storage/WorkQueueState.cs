using Nachos.Abstractions;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>
/// The work items and leases of one store, across workspaces: a lock-guarded table with the SQL provider's lease and
/// fencing semantics (see <see cref="Abstractions.Stores.IWorkQueue"/>). Every member must be called under
/// <see cref="Gate"/>.
/// </summary>
/// <remarks>
/// A lease is held while its row exists with the caller's owner and token: completing, failing or releasing removes the row, and
/// a reclaim replaces it with a higher token. Expiry alone does not end a lease; it only lets another owner take it.
/// </remarks>
internal sealed class WorkQueueState
{
    private const int MaxErrorLength = 4000;

    private readonly List<QueuedItem> _items = [];
    private readonly Dictionary<string, HeldLease> _leases = new(StringComparer.Ordinal);
    private long _lastItemId;
    private long _lastFencingToken;

    public Lock Gate { get; } = new();

    /// <summary>Enqueues an item with the next <c>Id</c>, built by <paramref name="create"/>, and returns the id.</summary>
    public long Enqueue(Func<long, QueuedItem> create)
    {
        _items.Add(create(++_lastItemId));
        return _lastItemId;
    }

    /// <summary>The workspace's items, in <c>Id</c> order.</summary>
    public IEnumerable<QueuedItem> ItemsOf(string workspaceName) =>
        _items.Where(item => string.Equals(item.WorkspaceName, workspaceName, StringComparison.Ordinal));

    /// <summary>Removes the given items; leases that claimed them fail their next check only if fenced separately.</summary>
    public void Remove(IReadOnlySet<QueuedItem> items) => _items.RemoveAll(items.Contains);

    /// <exception cref="LeaseLostException">The lease is no longer held, or it did not claim <paramref name="itemId"/>.</exception>
    public void RequireHeldWith(WorkLease lease, long itemId)
    {
        RequireHeld(lease);
        if (!_leases[lease.WorkUnitKey].ItemIds.Contains(itemId))
        {
            throw new LeaseLostException(lease.WorkUnitKey);
        }
    }

    /// <summary>
    /// The deletion job's first step (spec §13.1) for everything <paramref name="inTarget"/> selects, except the unit
    /// <paramref name="ownWorkUnitKey"/>: removes open non-deletion items, completes open deletion items with
    /// <paramref name="supersededReason"/>, and gives every lease on the selected units a new fencing token, so its
    /// holder's next check fails.
    /// </summary>
    public void CancelOtherWork(Func<QueuedItem, bool> inTarget, string ownWorkUnitKey, string supersededReason, DateTimeOffset now)
    {
        var others = _items
            .Where(item => inTarget(item) && !string.Equals(item.WorkUnitKey, ownWorkUnitKey, StringComparison.Ordinal))
            .ToList();
        foreach (var item in others.Where(item => item.IsOpen && item.TaskType == WorkTaskType.Deletion))
        {
            item.Status = WorkItemStatus.Succeeded;
            item.Error = supersededReason;
            item.CompletedAt = now;
        }

        Remove(others.Where(item => item.IsOpen).ToHashSet());
        foreach (var workUnitKey in others.Select(item => item.WorkUnitKey).Distinct(StringComparer.Ordinal))
        {
            if (_leases.TryGetValue(workUnitKey, out var lease))
            {
                _leases[workUnitKey] = lease with { FencingToken = ++_lastFencingToken };
            }
        }
    }

    /// <summary>A job's status, by the same buckets as <see cref="Status"/>.</summary>
    public JobStatus JobStatusOf(QueuedItem item, DateTimeOffset now) => item.Status switch
    {
        WorkItemStatus.Succeeded => JobStatus.Completed,
        WorkItemStatus.DeadLettered => JobStatus.DeadLettered,
        _ when IsTakenAt(item.WorkUnitKey, now) => JobStatus.InProgress,
        _ => JobStatus.Pending,
    };

    public WorkLease? TryAcquire(
        string owner,
        IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes,
        TimeSpan leaseDuration,
        DateTimeOffset now) =>
        DueUnits(taskTypes, now).FirstOrDefault() is ({ } workUnitKey, var claimable, var policy)
            ? Grant(workUnitKey, owner, leaseDuration, now, Batch(claimable, policy.BatchTokenTarget))
            : null;

    public QueueBacklog Backlog(IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, DateTimeOffset now)
    {
        var oldest = _items
            .Where(item => item.IsOpen && taskTypes.ContainsKey(item.TaskType))
            .Select(item => (DateTimeOffset?)item.CreatedAt)
            .Min();
        return new QueueBacklog(DueUnits(taskTypes, now).LongCount(), oldest);
    }

    public WorkLease? TryLease(string workUnitKey, string owner, TimeSpan leaseDuration, DateTimeOffset now) =>
        IsTakenAt(workUnitKey, now) ? null : Grant(workUnitKey, owner, leaseDuration, now, []);

    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    public void RequireHeld(WorkLease lease)
    {
        if (!_leases.TryGetValue(lease.WorkUnitKey, out var held)
            || !string.Equals(held.Owner, lease.Owner, StringComparison.Ordinal)
            || held.FencingToken != lease.FencingToken)
        {
            throw new LeaseLostException(lease.WorkUnitKey);
        }
    }

    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    public void Renew(WorkLease lease, TimeSpan leaseDuration, DateTimeOffset now)
    {
        RequireHeld(lease);
        _leases[lease.WorkUnitKey] = _leases[lease.WorkUnitKey] with { ExpiresAt = now + leaseDuration };
    }

    /// <param name="reason">Why the items succeeded without effect (such as <c>resource_deleted</c>), or null.</param>
    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    public void Complete(WorkLease lease, DateTimeOffset now, string? reason = null)
    {
        foreach (var item in Release(lease))
        {
            item.Status = WorkItemStatus.Succeeded;
            item.Error = reason ?? item.Error;
            item.CompletedAt = now;
        }
    }

    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    public void GiveBack(WorkLease lease, DateTimeOffset availableAt)
    {
        foreach (var item in Release(lease).Where(item => item.IsOpen))
        {
            item.Status = WorkItemStatus.Pending;
            item.Attempts = Math.Max(0, item.Attempts - 1);
            item.AvailableAt = availableAt;
        }
    }

    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    public void Fail(WorkLease lease, string reason, TimeSpan retryDelay, int maxAttempts, DateTimeOffset now)
    {
        var error = reason.Length > MaxErrorLength ? reason[..MaxErrorLength] : reason;
        foreach (var item in Release(lease))
        {
            item.Error = error;
            if (item.Attempts >= maxAttempts)
            {
                item.Status = WorkItemStatus.DeadLettered;
                item.CompletedAt = now;
            }
            else
            {
                item.Status = WorkItemStatus.Failed;
                item.AvailableAt = now + retryDelay;
            }
        }
    }

    public QueueStatusRecord Status(
        string workspaceName, string? observerName, string? senderName, string? sessionName, DateTimeOffset now)
    {
        var totals = new Tally();
        var sessions = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var selected = _items.Where(item =>
            string.Equals(item.WorkspaceName, workspaceName, StringComparison.Ordinal)
            && (observerName is null || item.Observers.Contains(observerName, StringComparer.Ordinal))
            && (senderName is null || string.Equals(item.SenderName, senderName, StringComparison.Ordinal))
            && (sessionName is null || string.Equals(item.SessionName, sessionName, StringComparison.Ordinal)));

        foreach (var item in selected)
        {
            var state = item.Status switch
            {
                WorkItemStatus.Succeeded => Bucket.Completed,
                WorkItemStatus.DeadLettered => Bucket.DeadLettered,
                _ when IsTakenAt(item.WorkUnitKey, now) => Bucket.InProgress,
                _ => Bucket.Pending,
            };
            totals.Add(state);
            if (item.SessionName is { } session)
            {
                (sessions.TryGetValue(session, out var tally) ? tally : sessions[session] = new Tally()).Add(state);
            }
        }

        return new QueueStatusRecord(
            totals.ToCounts(), sessions.ToDictionary(pair => pair.Key, pair => pair.Value.ToCounts(), StringComparer.Ordinal));
    }

    public int PruneSucceeded(TimeSpan retention, DateTimeOffset now) =>
        _items.RemoveAll(item => item.Status == WorkItemStatus.Succeeded && item.CompletedAt <= now - retention);

    /// <summary>
    /// The units <see cref="TryAcquire"/> could lease, oldest lowest open item first, each with its claimable items (in
    /// <c>Id</c> order, below the first one still backing off) and the policy of its lowest item's type.
    /// </summary>
    private IEnumerable<(string WorkUnitKey, List<QueuedItem> Claimable, WorkBatchPolicy Policy)> DueUnits(
        IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, DateTimeOffset now)
    {
        // _items is in Id order, so each unit's items, and the units by their lowest open item, come out oldest first.
        var units = _items
            .Where(item => item.IsOpen && taskTypes.ContainsKey(item.TaskType))
            .GroupBy(item => item.WorkUnitKey, StringComparer.Ordinal);

        foreach (var unit in units)
        {
            if (IsTakenAt(unit.Key, now) || unit.First().AvailableAt > now)
            {
                continue;
            }

            var claimable = unit.TakeWhile(item => item.AvailableAt <= now).ToList();
            var policy = taskTypes[claimable[0].TaskType];
            var isDue = claimable.Sum(item => (long)item.TokenCount) >= policy.EligibilityTokens
                || now - claimable.Min(item => item.CreatedAt) >= policy.MaxAge;
            if (isDue)
            {
                yield return (unit.Key, claimable, policy);
            }
        }
    }

    /// <summary>True while a lease on the unit has not expired (expired means strictly past <c>ExpiresAt</c>).</summary>
    private bool IsTakenAt(string workUnitKey, DateTimeOffset now) =>
        _leases.TryGetValue(workUnitKey, out var lease) && now <= lease.ExpiresAt;

    /// <summary>
    /// Items in <c>Id</c> order while their tokens fit <paramref name="tokenTarget"/> and their count
    /// <see cref="WorkLease.MaxItems"/>, and always the first.
    /// </summary>
    private static List<QueuedItem> Batch(List<QueuedItem> claimable, int tokenTarget)
    {
        var batch = new List<QueuedItem> { claimable[0] };
        long tokens = claimable[0].TokenCount;
        foreach (var item in claimable.Skip(1))
        {
            tokens += item.TokenCount;
            if (tokens > tokenTarget || batch.Count == WorkLease.MaxItems)
            {
                break;
            }

            batch.Add(item);
        }

        return batch;
    }

    private WorkLease Grant(
        string workUnitKey, string owner, TimeSpan leaseDuration, DateTimeOffset now, List<QueuedItem> items)
    {
        var fencingToken = ++_lastFencingToken;
        _leases[workUnitKey] = new HeldLease(owner, fencingToken, now + leaseDuration, items.Select(item => item.Id).ToHashSet());
        foreach (var item in items)
        {
            item.Attempts++;
        }

        return new WorkLease(workUnitKey, owner, fencingToken, [.. items.Select(item => item.ToRecord())]);
    }

    /// <summary>Ends a held lease and returns the items it claimed (by the store's record, not the caller's copy).</summary>
    private List<QueuedItem> Release(WorkLease lease)
    {
        RequireHeld(lease);
        var ids = _leases[lease.WorkUnitKey].ItemIds;
        _leases.Remove(lease.WorkUnitKey);
        return [.. _items.Where(item => ids.Contains(item.Id))];
    }

    private sealed record HeldLease(string Owner, long FencingToken, DateTimeOffset ExpiresAt, IReadOnlySet<long> ItemIds);

    private enum Bucket
    {
        Pending,
        InProgress,
        Completed,
        DeadLettered,
    }

    private sealed class Tally
    {
        private readonly long[] _counts = new long[4];

        public void Add(Bucket bucket) => _counts[(int)bucket]++;

        public QueueCounts ToCounts() => new(
            _counts[(int)Bucket.Pending],
            _counts[(int)Bucket.InProgress],
            _counts[(int)Bucket.Completed],
            _counts[(int)Bucket.DeadLettered]);
    }
}
