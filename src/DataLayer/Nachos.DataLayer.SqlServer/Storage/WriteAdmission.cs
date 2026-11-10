using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// The parent checks of the deletion write barrier (spec §13.1). Each must run inside the caller's transaction: its lock is
/// held until that transaction ends, so a deletion acceptance, whose <c>UPDATE</c> needs an exclusive lock on the same row,
/// waits for the write to commit, and a write that starts after the acceptance waits for it and then sees
/// <see cref="LifecycleState.Deleting"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock order</b> (every transaction): lease rows, then the workspace row, then session rows in <c>Id</c> order, then
/// child rows. A worker commit takes its lease row first. A deletion job step takes its own lease row and, when it fences
/// other work, every other lease row it bumps before any parent row, so it never waits for a lease while holding a parent:
/// otherwise a worker holding its lease and waiting for the workspace's S, queued behind an acceptance's X that waits for
/// the job's S, would close a cycle. Acceptance never touches lease rows.
/// </para>
/// <para>
/// Shared locks come from <c>WITH (REPEATABLEREAD)</c> alone, which locks even under read committed snapshot, never
/// combined with <c>HOLDLOCK</c> or <c>READCOMMITTEDLOCK</c>. Each locking read is <c>WHERE Id = @id</c> on the clustered
/// key, the id resolved first by a snapshot read, so the only lock taken is the parent row's own: no plan can scan and
/// S-lock every row. A writer that changes the parent row itself takes its exclusive lock directly
/// (<c>UPDATE … WHERE LifecycleState &lt;&gt; 2</c>), never S and then X: two such writers converting at once would deadlock.
/// </para>
/// <para>
/// Under autocommit a lock would end with its statement and protect nothing, so every locking read throws
/// <see cref="InvalidOperationException"/> when no transaction is open.
/// </para>
/// </remarks>
internal static class WriteAdmission
{
    public const byte Deleting = (byte)LifecycleState.Deleting;

    private const string SharedLock = "REPEATABLEREAD";

    private const string UpdateLock = "UPDLOCK, ROWLOCK";

    private const string WorkspacesTable = "dbo.Workspaces";

    private const string SessionsTable = "dbo.Sessions";

    /// <summary>Takes S on the workspace row and returns its id.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    /// <exception cref="ConflictException">The workspace is being deleted.</exception>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task<long> AdmitWorkspaceAsync(NachosDbContext db, string workspaceName, CancellationToken ct)
    {
        var workspace = await LockWorkspaceAsync(db, workspaceName, ct) ?? throw Lookups.WorkspaceNotFound(workspaceName);
        if (workspace.LifecycleState == Deleting)
        {
            throw WorkspaceDeleting();
        }

        return workspace.Id;
    }

    /// <summary>Takes S on the workspace row, then S on the session row, and returns the session's keys.</summary>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    /// <exception cref="ConflictException">The workspace or the session is being deleted.</exception>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task<SessionKey> AdmitSessionAsync(
        NachosDbContext db, string workspaceName, string sessionName, CancellationToken ct)
    {
        var workspaceId = await AdmitWorkspaceAsync(db, workspaceName, ct);
        var sessions = await AdmitSessionsAsync(db, workspaceName, workspaceId, [sessionName], ct);
        return new SessionKey(workspaceId, sessions[sessionName]);
    }

    /// <summary>
    /// Takes S on each named session's row, in <c>Id</c> order, and returns their ids by name. The caller has admitted the
    /// workspace (<see cref="AdmitWorkspaceAsync"/>) in the same transaction.
    /// </summary>
    /// <exception cref="NotFoundException">A session does not exist.</exception>
    /// <exception cref="ConflictException">A session is being deleted.</exception>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task<IReadOnlyDictionary<string, long>> AdmitSessionsAsync(
        NachosDbContext db, string workspaceName, long workspaceId, IEnumerable<string> sessionNames, CancellationToken ct)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var session in await LockSessionsAsync(db, workspaceId, sessionNames, ct))
        {
            if (session is not { Id: { } id, State: { } state })
            {
                throw Lookups.SessionNotFound(workspaceName, session.Name);
            }

            if (state == Deleting)
            {
                throw SessionDeleting();
            }

            ids[session.Name] = id;
        }

        return ids;
    }

    /// <summary>
    /// Takes S on the workspace row, then the session row's update lock, and returns the session's keys: the admission of
    /// membership writers, which serialize per session on that lock (see <see cref="Upserts"/>). An update lock excludes
    /// acceptance's exclusive lock as S does, so S as well would add nothing.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    /// <exception cref="ConflictException">The workspace or the session is being deleted.</exception>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task<SessionKey> AdmitSessionForMembershipAsync(
        NachosDbContext db, string workspaceName, string sessionName, CancellationToken ct)
    {
        var workspaceId = await AdmitWorkspaceAsync(db, workspaceName, ct);
        var sessionId = await FindSessionIdAsync(db, workspaceId, sessionName, ct)
            ?? throw Lookups.SessionNotFound(workspaceName, sessionName);
        await LockSessionAsync(db, workspaceName, sessionName, sessionId, ct);
        return new SessionKey(workspaceId, sessionId);
    }

    /// <summary>Holds the session row's update lock until the transaction ends, once it is shown not to be deleting.</summary>
    /// <exception cref="NotFoundException">The session no longer exists.</exception>
    /// <exception cref="ConflictException">The session is being deleted.</exception>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task LockSessionAsync(
        NachosDbContext db, string workspaceName, string sessionName, long sessionId, CancellationToken ct)
    {
        var state = await SessionStateAsync(db, sessionId, UpdateLock, ct) ?? throw Lookups.SessionNotFound(workspaceName, sessionName);
        if (state == Deleting)
        {
            throw SessionDeleting();
        }
    }

    /// <summary>
    /// The barrier of a worker commit (spec §13.1), taken after its fencing check: S on the workspace row, then on the row
    /// of each session in <paramref name="ownSessions"/> and <paramref name="namedSessions"/>, in <c>Id</c> order. True
    /// when the commit may write. False when its result must be discarded: the workspace is being deleted (or, for a
    /// caller that has not already required it, no longer exists), a session it works for (<paramref name="ownSessions"/>)
    /// is being deleted or no longer exists, or a session its result names (<paramref name="namedSessions"/>) is being
    /// deleted. A named session that does not exist is the caller's to reject.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task<bool> AdmitWorkerAsync(
        NachosDbContext db,
        string workspaceName,
        IReadOnlyCollection<string> ownSessions,
        IEnumerable<string> namedSessions,
        CancellationToken ct)
    {
        if (await LockWorkspaceAsync(db, workspaceName, ct) is not { LifecycleState: not Deleting } workspace)
        {
            return false;
        }

        var own = ownSessions.ToHashSet(StringComparer.Ordinal);
        var sessions = await LockSessionsAsync(db, workspace.Id, ownSessions.Concat(namedSessions), ct);
        return sessions.All(session => session.State is null ? !own.Contains(session.Name) : session.State != Deleting);
    }

    /// <summary>
    /// The barrier of a batch that writes under many sessions (the chunk backfill): S on every existing workspace of
    /// <paramref name="parents"/>, then on every existing session, each in <c>Id</c> order. The states are left to the
    /// caller's write, which must skip rows under a parent being deleted; read after these locks, they cannot change
    /// before commit.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task LockParentsAsync(
        NachosDbContext db, IEnumerable<(string Workspace, string Session)> parents, CancellationToken ct)
    {
        RequireTransaction(db);
        var pairs = parents.Distinct().ToList();
        var workspaces = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in pairs.Select(pair => pair.Workspace).Distinct(StringComparer.Ordinal))
        {
            if (await Lookups.FindWorkspaceIdAsync(db, name, ct) is { } id)
            {
                workspaces[name] = id;
            }
        }

        foreach (var id in workspaces.Values.Order())
        {
            await WorkspaceStateAsync(db, id, ct);
        }

        var sessionIds = new SortedSet<long>();
        foreach (var (workspace, session) in pairs)
        {
            if (workspaces.TryGetValue(workspace, out var workspaceId) && await FindSessionIdAsync(db, workspaceId, session, ct) is { } id)
            {
                sessionIds.Add(id);
            }
        }

        foreach (var id in sessionIds)
        {
            await SessionStateAsync(db, id, SharedLock, ct);
        }
    }

    /// <summary>
    /// The ids of a deletion job's target, by snapshot reads (no locks): what a job step resolves before it takes any lock,
    /// since lease rows come before parent rows.
    /// </summary>
    /// <exception cref="LeaseLostException">The target no longer exists, so the lease cannot be its deletion lease.</exception>
    public static async Task<DeletionScope> FindDeletionScopeAsync(
        NachosDbContext db, WorkLease lease, DeletionTarget target, CancellationToken ct)
    {
        var workspaceId = await Lookups.FindWorkspaceIdAsync(db, target.WorkspaceName, ct) ?? throw new LeaseLostException(lease.WorkUnitKey);
        if (target.SessionName is not { } sessionName)
        {
            return new DeletionScope(workspaceId, SessionId: null);
        }

        var sessionId = await FindSessionIdAsync(db, workspaceId, sessionName, ct) ?? throw new LeaseLostException(lease.WorkUnitKey);
        return new DeletionScope(workspaceId, sessionId);
    }

    /// <summary>
    /// The barrier of a deletion job's cancel and purge steps (spec §13.1, the deletion-maintenance exemption): S on the
    /// workspace row, then, for a session target, S on the session row, both held to commit; the target must be being
    /// deleted by one of the lease's items. The caller already holds its lease row (and any other lease row it takes), so
    /// this runs after every lease row of the transaction.
    /// </summary>
    /// <exception cref="LeaseLostException">The target is gone, not being deleted, or being deleted by another item.</exception>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task AdmitDeletionJobAsync(NachosDbContext db, WorkLease lease, DeletionScope scope, CancellationToken ct)
    {
        if (scope.SessionId is not { } sessionId)
        {
            RequireJob(await ReadParentAsync(db, WorkspacesTable, scope.WorkspaceId, ct), lease);
            return;
        }

        _ = await ReadParentAsync(db, WorkspacesTable, scope.WorkspaceId, ct) ?? throw new LeaseLostException(lease.WorkUnitKey);
        RequireJob(await ReadParentAsync(db, SessionsTable, sessionId, ct), lease);
    }

    /// <summary>
    /// The barrier of a deletion job's removal: for a session target, S on the workspace row and then X on the session row;
    /// for a workspace target, X on the workspace row. Each X is taken directly by an <c>UPDATE</c>, never S first. The
    /// target must be being deleted by one of the lease's items. The caller already holds its lease row.
    /// </summary>
    /// <exception cref="LeaseLostException">The target is gone, not being deleted, or being deleted by another item.</exception>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task LockDeletionTargetAsync(NachosDbContext db, WorkLease lease, DeletionScope scope, CancellationToken ct)
    {
        if (scope.SessionId is not { } sessionId)
        {
            RequireJob(await LockParentAsync(db, WorkspacesTable, scope.WorkspaceId, ct), lease);
            return;
        }

        _ = await ReadParentAsync(db, WorkspacesTable, scope.WorkspaceId, ct) ?? throw new LeaseLostException(lease.WorkUnitKey);
        RequireJob(await LockParentAsync(db, SessionsTable, sessionId, ct), lease);
    }

    public static ConflictException WorkspaceDeleting() => new("workspace is being deleted");

    public static ConflictException SessionDeleting() => new("session is being deleted");

    /// <summary>
    /// The workspace's id and state, its row read under S held to commit; null when it does not exist (or no longer does).
    /// The id comes from a snapshot read, so the locking read is by clustered key.
    /// </summary>
    private static async Task<ParentState?> LockWorkspaceAsync(NachosDbContext db, string workspaceName, CancellationToken ct)
    {
        RequireTransaction(db);
        if (await Lookups.FindWorkspaceIdAsync(db, workspaceName, ct) is not { } id)
        {
            return null;
        }

        return await WorkspaceStateAsync(db, id, ct) is { } state ? new ParentState(id, state) : null;
    }

    /// <summary>The workspace's state, read by key under S held to commit; null when it is gone.</summary>
    private static async Task<byte?> WorkspaceStateAsync(NachosDbContext db, long workspaceId, CancellationToken ct)
    {
        RequireTransaction(db);
        return (await db.Database
                .SqlQueryRaw<byte>(
                    "SELECT LifecycleState AS [Value] FROM dbo.Workspaces WITH (REPEATABLEREAD) WHERE Id = @workspace",
                    SqlParameters.Long("@workspace", workspaceId))
                .ToListAsync(ct))
            .Cast<byte?>()
            .SingleOrDefault();
    }

    /// <summary>
    /// Each distinct name with its session's id and state, the rows read under S in <c>Id</c> order (missing sessions have
    /// neither). Resolving every name before the first lock keeps the order whatever order the names come in.
    /// </summary>
    private static async Task<List<LockedSession>> LockSessionsAsync(
        NachosDbContext db, long workspaceId, IEnumerable<string> sessionNames, CancellationToken ct)
    {
        RequireTransaction(db);
        var sessions = new List<LockedSession>();
        foreach (var name in sessionNames.Distinct(StringComparer.Ordinal))
        {
            sessions.Add(new LockedSession(name, await FindSessionIdAsync(db, workspaceId, name, ct), State: null));
        }

        sessions.Sort((left, right) => Nullable.Compare(left.Id, right.Id));
        for (var i = 0; i < sessions.Count; i++)
        {
            if (sessions[i].Id is { } id)
            {
                sessions[i] = sessions[i] with { State = await SessionStateAsync(db, id, SharedLock, ct) };
            }
        }

        return sessions;
    }

    private static Task<long?> FindSessionIdAsync(NachosDbContext db, long workspaceId, string sessionName, CancellationToken ct) =>
        db.Sessions.Named(workspaceId, sessionName).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);

    /// <summary>The session's state, read by key under <paramref name="hints"/> (one of the constants above); null when it is gone.</summary>
    private static async Task<byte?> SessionStateAsync(NachosDbContext db, long sessionId, string hints, CancellationToken ct)
    {
        RequireTransaction(db);
        return (await db.Database
                .SqlQueryRaw<byte>(
                    string.Concat("SELECT LifecycleState AS [Value] FROM dbo.Sessions WITH (", hints, ") WHERE Id = @session"),
                    SqlParameters.Long("@session", sessionId))
                .ToListAsync(ct))
            .Cast<byte?>()
            .SingleOrDefault();
    }

    /// <summary>
    /// A parent row's state and job, read by key under S held to commit; null when it is gone. <c>table</c> is one of the
    /// table constants above, never input.
    /// </summary>
    private static async Task<DeletionParent?> ReadParentAsync(NachosDbContext db, string table, long id, CancellationToken ct)
    {
        RequireTransaction(db);
        return (await db.Database
                .SqlQueryRaw<DeletionParent>(
                    string.Concat("SELECT LifecycleState, DeletionJobId FROM ", table, " WITH (", SharedLock, ") WHERE Id = @id"),
                    SqlParameters.Long("@id", id))
                .ToListAsync(ct))
            .SingleOrDefault();
    }

    /// <summary>
    /// A parent row's state and job, its exclusive lock taken by an <c>UPDATE</c> and held to commit; null when it is gone.
    /// <c>table</c> is one of the table constants above, never input.
    /// </summary>
    private static async Task<DeletionParent?> LockParentAsync(NachosDbContext db, string table, long id, CancellationToken ct)
    {
        RequireTransaction(db);
        return (await db.Database
                .SqlQueryRaw<DeletionParent>(
                    string.Concat(
                        "UPDATE ", table, " SET LifecycleVersion = LifecycleVersion + 1 OUTPUT inserted.LifecycleState, inserted.DeletionJobId WHERE Id = @id"),
                    SqlParameters.Long("@id", id))
                .ToListAsync(ct))
            .SingleOrDefault();
    }

    /// <exception cref="LeaseLostException">The parent is not being deleted by one of the lease's items.</exception>
    private static void RequireJob(DeletionParent? parent, WorkLease lease)
    {
        if (parent is not { LifecycleState: Deleting, DeletionJobId: { } jobId } || !lease.Items.Any(item => item.Id == jobId))
        {
            throw new LeaseLostException(lease.WorkUnitKey);
        }
    }

    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    private static void RequireTransaction(NachosDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The deletion write barrier must run inside the write's transaction.");
        }
    }

    /// <summary>A parent row's id and <c>LifecycleState</c>.</summary>
    private sealed record ParentState(long Id, byte LifecycleState);

    /// <summary>A session named by a write: its id and state, both null when it does not exist (or no longer does).</summary>
    private sealed record LockedSession(string Name, long? Id, byte? State);

    /// <summary>A deletion target's row as a job step reads it.</summary>
    private sealed record DeletionParent(byte LifecycleState, long? DeletionJobId);
}

/// <summary>The keys of a deletion job's target: a workspace, or a session of it when <see cref="SessionId"/> is set.</summary>
internal readonly record struct DeletionScope(long WorkspaceId, long? SessionId);
