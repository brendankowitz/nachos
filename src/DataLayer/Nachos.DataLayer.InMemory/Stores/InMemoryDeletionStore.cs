using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

/// <remarks>
/// <para>
/// Every member runs under the target workspace's gate, then the queue gate, then (for the workspace row and the grants)
/// <see cref="InMemoryState.Gate"/>, in the store's lock order, so acceptance is atomic with every write it bars, and
/// each job step's lease check is atomic with the step.
/// </para>
/// <para>
/// The purge needs no stored checkpoint: each batch deletes the first rows still present in the documented order, so a
/// job that resumes under a new lease continues exactly where the previous one stopped.
/// </para>
/// </remarks>
internal sealed class InMemoryDeletionStore(InMemoryState state) : IDeletionStore
{
    private const string Superseded = "superseded_by_workspace_deletion";

    public Task<DeletionAcceptance> BeginDeleteSessionAsync(string workspaceName, string sessionName, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    workspace.RequireNotDeleting();
                    var session = workspace.RequireSession(sessionName);
                    if (session.DeletionJobId is { } existing)
                    {
                        return new DeletionAcceptance(existing, AlreadyDeleting: true);
                    }

                    var jobId = Enqueue(DeletionTarget.Session(workspaceName, sessionName));
                    session.MarkDeleting(jobId);
                    return new DeletionAcceptance(jobId, AlreadyDeleting: false);
                }
            },
            ct);

    public Task<DeletionAcceptance> BeginDeleteWorkspaceAsync(string workspaceName, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    if (workspace.DeletionJobId is { } existing)
                    {
                        return new DeletionAcceptance(existing, AlreadyDeleting: true);
                    }

                    if (workspace.Sessions.Values.Any(session => session.Record.State == LifecycleState.Active))
                    {
                        throw new ConflictException("workspace has active sessions");
                    }

                    var jobId = Enqueue(DeletionTarget.Workspace(workspaceName));
                    lock (state.Gate)
                    {
                        workspace.MarkDeleting(jobId);
                    }

                    return new DeletionAcceptance(jobId, AlreadyDeleting: false);
                }
            },
            ct);

    public Task CancelOtherWorkAsync(WorkLease lease, DeletionTarget target, CancellationToken ct) =>
        state.Run(
            () => WithJobLease(lease, target, _ =>
                state.Queue.CancelOtherWork(InTarget(target), lease.WorkUnitKey, Superseded, state.Clock.GetUtcNow())),
            ct);

    public Task<bool> PurgeBatchAsync(WorkLease lease, DeletionTarget target, int batchSize, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
                var more = false;
                WithJobLease(lease, target, workspace => more = Purge(workspace, target, lease.WorkUnitKey, batchSize));
                return more;
            },
            ct);

    public Task RemoveAsync(WorkLease lease, DeletionTarget target, CancellationToken ct) =>
        state.Run(
            () => WithJobLease(lease, target, workspace =>
            {
                Purge(workspace, target, lease.WorkUnitKey, int.MaxValue);
                state.Queue.Complete(lease, state.Clock.GetUtcNow());
                if (target.SessionName is { } sessionName)
                {
                    workspace.Sessions.Remove(sessionName);
                    return;
                }

                // The workspace's own job leaves with it, so a new workspace of the same name lists no jobs.
                state.Queue.Remove(state.Queue.ItemsOf(workspace.Name).ToHashSet());
                lock (state.Gate)
                {
                    state.Workspaces.Remove(workspace.Name);
                }
            }),
            ct);

    public Task<Page<JobRecord>> ListJobsAsync(string workspaceName, PageRequest page, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(page);
                state.RequireWorkspace(workspaceName);
                lock (state.Queue.Gate)
                {
                    var now = state.Clock.GetUtcNow();
                    var jobs = state.Queue.ItemsOf(workspaceName).Where(item => item.TaskType == WorkTaskType.Deletion);
                    return Paging.ToPage(jobs, page, item => new JobRecord(
                        item.Id,
                        item.TaskType,
                        DeletionPayload.Read(item.ToRecord()),
                        state.Queue.JobStatusOf(item, now),
                        item.Attempts,
                        item.Error,
                        item.CreatedAt,
                        item.CompletedAt));
                }
            },
            ct);

    /// <summary>Enqueues the target's deletion item and returns its id. The caller holds the workspace gate.</summary>
    private long Enqueue(DeletionTarget target)
    {
        var now = state.Clock.GetUtcNow();
        lock (state.Queue.Gate)
        {
            return state.Queue.Enqueue(id => new QueuedItem(
                id,
                target.WorkspaceName,
                WorkTaskType.Deletion,
                target.WorkUnitKey,
                DeletionPayload.Create(target.Kind),
                tokenCount: 0,
                target.SessionName,
                messageId: null,
                senderName: null,
                observers: [],
                now));
        }
    }

    /// <summary>
    /// Runs one job step under the workspace gate and the queue gate, once the lease is shown to be held and to hold the
    /// target's deletion item.
    /// </summary>
    /// <exception cref="LeaseLostException">It is not.</exception>
    private void WithJobLease(WorkLease lease, DeletionTarget target, Action<WorkspaceEntry> step)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(target);
        var workspace = state.FindWorkspace(target.WorkspaceName) ?? throw new LeaseLostException(lease.WorkUnitKey);
        lock (workspace.Gate)
        {
            lock (state.Queue.Gate)
            {
                var jobId = target.SessionName is { } sessionName
                    ? workspace.Sessions.GetValueOrDefault(sessionName)?.DeletionJobId
                    : workspace.DeletionJobId;
                state.Queue.RequireHeldWith(lease, jobId ?? throw new LeaseLostException(lease.WorkUnitKey));
                step(workspace);
            }
        }
    }

    private static Func<QueuedItem, bool> InTarget(DeletionTarget target) =>
        item => string.Equals(item.WorkspaceName, target.WorkspaceName, StringComparison.Ordinal)
            && (target.SessionName is null || string.Equals(item.SessionName, target.SessionName, StringComparison.Ordinal));

    /// <summary>
    /// Deletes up to <paramref name="budget"/> rows in the purge order, and says whether any are left. The caller holds the
    /// workspace gate and the queue gate.
    /// </summary>
    private bool Purge(WorkspaceEntry workspace, DeletionTarget target, string ownWorkUnitKey, int budget)
    {
        var steps = PurgeSteps(workspace, target, ownWorkUnitKey);
        foreach (var step in steps)
        {
            foreach (var deleteRow in step().Take(budget))
            {
                deleteRow();
                budget--;
            }

            if (budget == 0)
            {
                break;
            }
        }

        return steps.Any(step => step().Count > 0);
    }

    /// <summary>
    /// The purge order (see <see cref="IDeletionStore.PurgeBatchAsync"/>): each step lists one deletion per row it would
    /// delete now. A step is read only after every earlier step is empty, so it sees their deletions.
    /// </summary>
    private List<Func<IReadOnlyList<Action>>> PurgeSteps(WorkspaceEntry workspace, DeletionTarget target, string ownWorkUnitKey)
    {
        if (target.SessionName is { } sessionName)
        {
            return workspace.Sessions.TryGetValue(sessionName, out var session) ? SessionSteps(workspace, session, ownWorkUnitKey) : [];
        }

        var steps = new List<Func<IReadOnlyList<Action>>>();
        foreach (var session in workspace.Sessions.Values.OrderBy(session => session.Order).ToList())
        {
            steps.AddRange(SessionSteps(workspace, session, ownWorkUnitKey));
            steps.Add(() => workspace.Sessions.ContainsKey(session.Record.Name) ? [() => workspace.Sessions.Remove(session.Record.Name)] : []);
        }

        steps.Add(() => Each(workspace.Conclusions.Keys, id => workspace.Conclusions.Remove(id)));
        steps.Add(() => Each(workspace.Collections.Keys, key => workspace.Collections.Remove(key)));
        steps.Add(() => Each(workspace.PeerCards.Keys, key => workspace.PeerCards.Remove(key)));
        steps.Add(() => Each(workspace.Idempotency.Keys, key => workspace.Idempotency.Remove(key)));
        steps.Add(() =>
        {
            lock (state.Gate)
            {
                return Each(
                    state.Grants.Where(grant => string.Equals(grant.WorkspaceName, workspace.Name, StringComparison.Ordinal)),
                    grant => RemoveGrant(grant));
            }
        });
        steps.Add(() => WorkItems(workspace.Name, sessionName: null, ownWorkUnitKey));
        steps.Add(() => Each(workspace.Peers.Keys, name => workspace.Peers.Remove(name)));
        return steps;
    }

    private void RemoveGrant(GrantRecord grant)
    {
        lock (state.Gate)
        {
            state.Grants.Remove(grant);
        }
    }

    /// <summary>A session's purge steps: chunks, summaries, its conclusions, unsupported tombstones, work items, members, messages.</summary>
    private List<Func<IReadOnlyList<Action>>> SessionSteps(WorkspaceEntry workspace, SessionEntry session, string ownWorkUnitKey)
    {
        var name = session.Record.Name;
        return
        [
            () => Each(session.Chunks.Keys, key => session.Chunks.Remove(key)),
            () => Each(session.Summaries.Keys, kind => session.Summaries.Remove(kind)),
            () => Each(
                workspace.Conclusions.Values.Where(c => string.Equals(c.Record.SessionName, name, StringComparison.Ordinal)).Select(c => c.Record.PublicId),
                id => workspace.Conclusions.Remove(id)),
            () => Each(
                workspace.Conclusions.Values
                    .Where(c => c.IsLive && c.Record.SourceIds.Count > 0 && !c.Record.SourceIds.Any(workspace.Conclusions.ContainsKey))
                    .Select(c => c.Record.PublicId),
                id => workspace.Conclusions[id] = workspace.Conclusions[id] with { DeletedAt = state.Clock.GetUtcNow() }),
            () => WorkItems(workspace.Name, name, ownWorkUnitKey),
            () => Each(session.Members.Keys, peer => session.Members.Remove(peer)),
            () => Enumerable.Range(0, session.Messages.Count).Select(_ => (Action)session.RemoveLastMessage).ToList(),
        ];
    }

    /// <summary>The work items of the workspace (or of one of its sessions) other than the job's own unit.</summary>
    private List<Action> WorkItems(string workspaceName, string? sessionName, string ownWorkUnitKey) =>
        Each(
            state.Queue.ItemsOf(workspaceName).Where(item =>
                (sessionName is null || string.Equals(item.SessionName, sessionName, StringComparison.Ordinal))
                && !string.Equals(item.WorkUnitKey, ownWorkUnitKey, StringComparison.Ordinal)),
            item => state.Queue.Remove(new HashSet<QueuedItem> { item }));

    /// <summary>One deletion per key, over a snapshot of the keys.</summary>
    private static List<Action> Each<T>(IEnumerable<T> keys, Action<T> delete) =>
        [.. keys.ToList().Select(key => (Action)(() => delete(key)))];
}
