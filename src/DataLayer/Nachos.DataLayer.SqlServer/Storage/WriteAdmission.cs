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
/// <b>Lock order</b> (every transaction): the workspace row, then session rows in <c>Id</c> order, then child rows. A
/// worker commit takes its lease row first; acceptance never touches lease rows, so the two cannot form a cycle.
/// </para>
/// <para>
/// Shared locks come from <c>WITH (REPEATABLEREAD)</c> alone, which locks even under read committed snapshot, never
/// combined with <c>HOLDLOCK</c> or <c>READCOMMITTEDLOCK</c>. Each locking read seeks the clustered key by <c>Id</c> (the
/// name is resolved by a snapshot read), so the only lock taken is the parent row's own. A writer that changes the parent
/// row itself takes its exclusive lock directly (<c>UPDATE … WHERE LifecycleState &lt;&gt; 2</c>), never S and then X:
/// two such writers converting at once would deadlock.
/// </para>
/// </remarks>
internal static class WriteAdmission
{
    public const byte Deleting = (byte)LifecycleState.Deleting;

    private const string SharedLock = "REPEATABLEREAD";

    private const string UpdateLock = "UPDLOCK, ROWLOCK";

    /// <summary>Takes S on the workspace row and returns its id.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    /// <exception cref="ConflictException">The workspace is being deleted.</exception>
    public static async Task<long> AdmitWorkspaceAsync(NachosDbContext db, string workspaceName, CancellationToken ct)
    {
        var workspace = await LockWorkspaceAsync(db, workspaceName, ct) ?? throw Lookups.WorkspaceNotFound(workspaceName);
        return workspace.LifecycleState == Deleting ? throw WorkspaceDeleting() : workspace.Id;
    }

    /// <summary>Takes S on the workspace row, then S on the session row, and returns the session's keys.</summary>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    /// <exception cref="ConflictException">The workspace or the session is being deleted.</exception>
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

            ids[session.Name] = state == Deleting ? throw SessionDeleting() : id;
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
    /// when the commit may write. False when its result must be discarded: the workspace is being deleted or is gone, a
    /// session it works for (<paramref name="ownSessions"/>) is being deleted or is gone, or a session its result names
    /// (<paramref name="namedSessions"/>) is being deleted. A named session that does not exist is the caller's to reject.
    /// </summary>
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
    public static async Task LockParentsAsync(
        NachosDbContext db, IEnumerable<(string Workspace, string Session)> parents, CancellationToken ct)
    {
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
            await db.Database
                .SqlQueryRaw<byte>(
                    "SELECT LifecycleState AS [Value] FROM dbo.Workspaces WITH (REPEATABLEREAD) WHERE Id = @workspace",
                    SqlParameters.Long("@workspace", id))
                .ToListAsync(ct);
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

    public static ConflictException WorkspaceDeleting() => new("workspace is being deleted");

    public static ConflictException SessionDeleting() => new("session is being deleted");

    /// <summary>The workspace's id and state, read under S held to commit; null when it does not exist.</summary>
    private static async Task<ParentState?> LockWorkspaceAsync(NachosDbContext db, string workspaceName, CancellationToken ct) =>
        (await db.Database
            .SqlQueryRaw<ParentState>(
                """
                SELECT w.Id, w.LifecycleState FROM dbo.Workspaces AS w WITH (REPEATABLEREAD)
                WHERE w.Id = (SELECT n.Id FROM dbo.Workspaces AS n WHERE n.Name = @name AND DATALENGTH(n.Name) = DATALENGTH(@name))
                """,
                SqlParameters.Text("@name", workspaceName))
            .ToListAsync(ct))
        .SingleOrDefault();

    /// <summary>
    /// Each distinct name with its session's id and state, the rows read under S in <c>Id</c> order (missing sessions have
    /// neither). Resolving every name before the first lock keeps the order whatever order the names come in.
    /// </summary>
    private static async Task<List<LockedSession>> LockSessionsAsync(
        NachosDbContext db, long workspaceId, IEnumerable<string> sessionNames, CancellationToken ct)
    {
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
    private static async Task<byte?> SessionStateAsync(NachosDbContext db, long sessionId, string hints, CancellationToken ct) =>
        (await db.Database
            .SqlQueryRaw<byte>(
                string.Concat("SELECT LifecycleState AS [Value] FROM dbo.Sessions WITH (", hints, ") WHERE Id = @session"),
                SqlParameters.Long("@session", sessionId))
            .ToListAsync(ct))
        .Cast<byte?>()
        .SingleOrDefault();

    /// <summary>A parent row's id and <c>LifecycleState</c>.</summary>
    private sealed record ParentState(long Id, byte LifecycleState);

    /// <summary>A session named by a write: its id and state, both null when it does not exist (or no longer does).</summary>
    private sealed record LockedSession(string Name, long? Id, byte? State);
}
