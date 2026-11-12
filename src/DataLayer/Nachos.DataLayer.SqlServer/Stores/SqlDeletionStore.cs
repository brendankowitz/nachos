using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <summary>
/// Deletion acceptance, the deletion job's steps and job listing (spec §13.1).
/// </summary>
/// <remarks>
/// <para>
/// Each acceptance is one transaction that takes the target row's exclusive lock <b>first</b>, with an <c>UPDATE</c> that
/// bumps <c>LifecycleVersion</c>, before any predicate is evaluated: it waits for every writer holding the row's S lock
/// (<see cref="WriteAdmission"/>) and blocks every later one until commit. A session's acceptance first takes the
/// workspace's S lock, in the lock order workspace → session.
/// </para>
/// <para>
/// A repeated acceptance of a target already being deleted rolls back (nothing changes) and returns the recorded job.
/// </para>
/// <para>
/// Each job step is one transaction that takes lease rows first (its own fence, and in the cancel step the leases it
/// fences), then the target's parent rows (<see cref="WriteAdmission"/>), then child rows: the lock order of every lease
/// writer (<see cref="SqlWorkQueue"/>). A purge batch deletes at most its batch size of rows, child first, each statement
/// by <c>TOP</c>, so no batch escalates to a table lock at the default size.
/// </para>
/// </remarks>
internal sealed class SqlDeletionStore(SqlStoreRuntime runtime) : IDeletionStore
{
    private const string Superseded = "superseded_by_workspace_deletion";

    public async Task<DeletionAcceptance> BeginDeleteSessionAsync(string workspaceName, string sessionName, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(workspaceName);
        ArgumentNullException.ThrowIfNull(sessionName);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var workspaceId = await WriteAdmission.AdmitWorkspaceAsync(db, workspaceName, ct);
        var session = (await db.Database
                .SqlQueryRaw<LifecycleRow>(
                    """
                    UPDATE dbo.Sessions SET LifecycleVersion = LifecycleVersion + 1
                    OUTPUT inserted.Id, inserted.LifecycleState, inserted.DeletionJobId
                    WHERE WorkspaceId = @workspace AND Name = @name AND DATALENGTH(Name) = DATALENGTH(@name)
                    """,
                    SqlParameters.Long("@workspace", workspaceId),
                    SqlParameters.Text("@name", sessionName))
                .ToListAsync(ct))
            .SingleOrDefault() ?? throw Lookups.SessionNotFound(workspaceName, sessionName);
        if (session.AlreadyAccepted() is { } existing)
        {
            await transaction.RollbackAsync(ct);
            return existing;
        }

        await PauseBeforeTransitionAsync(ct);
        var jobId = await EnqueueAsync(db, workspaceId, session.Id, DeletionTarget.Session(workspaceName, sessionName), ct);
        await MarkDeletingAsync(db, "dbo.Sessions", session.Id, jobId, ct);
        await transaction.CommitAsync(ct);
        return new DeletionAcceptance(jobId, AlreadyDeleting: false);
    }

    public async Task<DeletionAcceptance> BeginDeleteWorkspaceAsync(string workspaceName, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(workspaceName);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var workspace = (await db.Database
                .SqlQueryRaw<LifecycleRow>(
                    """
                    UPDATE dbo.Workspaces SET LifecycleVersion = LifecycleVersion + 1
                    OUTPUT inserted.Id, inserted.LifecycleState, inserted.DeletionJobId
                    WHERE Name = @name AND DATALENGTH(Name) = DATALENGTH(@name)
                    """,
                    SqlParameters.Text("@name", workspaceName))
                .ToListAsync(ct))
            .SingleOrDefault() ?? throw Lookups.WorkspaceNotFound(workspaceName);
        if (workspace.AlreadyAccepted() is { } existing)
        {
            await transaction.RollbackAsync(ct);
            return existing;
        }

        // With X held, no session can be created or reactivated: those writers need the workspace's S lock first. This
        // statement's snapshot starts after X was granted, so it sees every one that committed before.
        var active = await db.Database
            .SqlQueryRaw<int>(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Sessions WHERE WorkspaceId = @workspace AND LifecycleState = 0) THEN 1 ELSE 0 END AS [Value]",
                SqlParameters.Long("@workspace", workspace.Id))
            .ToListAsync(ct);
        if (active.Single() == 1)
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException("workspace has active sessions");
        }

        await PauseBeforeTransitionAsync(ct);
        var jobId = await EnqueueAsync(db, workspace.Id, sessionId: null, DeletionTarget.Workspace(workspaceName), ct);
        await MarkDeletingAsync(db, "dbo.Workspaces", workspace.Id, jobId, ct);
        await transaction.CommitAsync(ct);
        return new DeletionAcceptance(jobId, AlreadyDeleting: false);
    }

    /// <remarks>
    /// One transaction, in the lock order of every lease writer: the job's own lease row (the fence); then, by snapshot
    /// reads, the target's keys and the units of its open items; then each of those units' lease rows, bumped one at a
    /// time (other deletion units first, then the rest, each in ordinal order, so two jobs whose sets overlap take them in
    /// the same order); only then the parent rows (<see cref="WriteAdmission.AdmitDeletionJobAsync"/>), and last the items.
    /// The step never waits for a lease row while it holds a parent row.
    /// </remarks>
    public async Task CancelOtherWorkAsync(WorkLease lease, DeletionTarget target, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(target);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await SqlWorkQueue.RequireHeldAsync(db, lease, ct);
        var scope = await WriteAdmission.FindDeletionScopeAsync(db, lease, target, ct);
        await FenceOtherLeasesAsync(db, lease, scope, ct);
        await WriteAdmission.AdmitDeletionJobAsync(db, lease, scope, ct);

        await db.Database.ExecuteSqlRawAsync(
            string.Concat(
                "DELETE i FROM dbo.WorkItems AS i WITH (FORCESEEK) WHERE ",
                ItemsOf(scope, "i"),
                " AND i.Status IN (0, 1) AND i.TaskType <> @deletion"),
            [.. ScopeParameters(scope), SqlParameters.Byte("@deletion", (byte)WorkTaskType.Deletion)],
            ct);
        if (scope.SessionId is null)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE i SET Status = 2, Error = @superseded, CompletedAt = @now
                FROM dbo.WorkItems AS i WITH (FORCESEEK)
                WHERE i.WorkspaceId = @workspace AND i.Status IN (0, 1) AND i.TaskType = @deletion
                  AND NOT (i.WorkUnitKey = @own AND DATALENGTH(i.WorkUnitKey) = DATALENGTH(@own))
                """,
                [
                    .. ScopeParameters(scope),
                    SqlParameters.Byte("@deletion", (byte)WorkTaskType.Deletion),
                    SqlParameters.Text("@superseded", Superseded),
                    SqlParameters.Time("@now", runtime.Clock.GetUtcNow()),
                    SqlParameters.Text("@own", lease.WorkUnitKey),
                ],
                ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <remarks>
    /// One transaction: the fence, the parent rows' S locks (<see cref="WriteAdmission.AdmitDeletionJobAsync"/>), then one
    /// SQL batch that deletes <c>TOP</c> the rows left in each step in turn (<see cref="SessionSteps"/>,
    /// <see cref="WorkspaceSteps"/>) and reports whether any are left. The progress is the set of rows left, so a resumed
    /// job needs no stored checkpoint and nothing of the worker that stopped. The dependents of each batch's removed
    /// conclusions are re-evaluated in the same transaction, so a lost lease cannot strand them.
    /// </remarks>
    public async Task<bool> PurgeBatchAsync(WorkLease lease, DeletionTarget target, int batchSize, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await SqlWorkQueue.RequireHeldAsync(db, lease, ct);
        var scope = await WriteAdmission.FindDeletionScopeAsync(db, lease, target, ct);
        await WriteAdmission.AdmitDeletionJobAsync(db, lease, scope, ct);
        var more = await PurgeAsync(db, lease, scope, batchSize, ct);
        await transaction.CommitAsync(ct);
        return more;
    }

    /// <remarks>
    /// One transaction: the fence (the job's own lease row), then X on the target's row
    /// (<see cref="WriteAdmission.LockDeletionTargetAsync"/>), then whatever rows are left, the row itself and the lease.
    /// A session's deletion item stays, as its record in <c>W/jobs</c>; a workspace's items all go with it, since they
    /// reference its row.
    /// </remarks>
    public async Task RemoveAsync(WorkLease lease, DeletionTarget target, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(target);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await SqlWorkQueue.RequireHeldAsync(db, lease, ct);
        var scope = await WriteAdmission.FindDeletionScopeAsync(db, lease, target, ct);
        await WriteAdmission.LockDeletionTargetAsync(db, lease, scope, ct);
        await PurgeAsync(db, lease, scope, int.MaxValue, ct);

        if (scope.SessionId is { } sessionId)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE dbo.Sessions WHERE Id = @session", [SqlParameters.Long("@session", sessionId)], ct);
            await SqlWorkQueue.CompleteItemsAsync(db, lease, runtime.Clock.GetUtcNow(), error: null, ct);
            await SqlWorkQueue.FreeLeaseAsync(db, lease, ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE i FROM dbo.WorkItems AS i WITH (FORCESEEK) WHERE i.WorkspaceId = @workspace",
                [SqlParameters.Long("@workspace", scope.WorkspaceId)],
                ct);
            await SqlWorkQueue.FreeLeaseAsync(db, lease, ct);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE dbo.Workspaces WHERE Id = @workspace", [SqlParameters.Long("@workspace", scope.WorkspaceId)], ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <remarks>
    /// Two snapshot statements (the count and the page). An open item is in progress while its unit's lease is unexpired,
    /// as in <see cref="SqlWorkQueue.GetStatusAsync"/>.
    /// </remarks>
    public async Task<Page<JobRecord>> ListJobsAsync(string workspaceName, PageRequest page, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(page);
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var rows = db.Database.SqlQueryRaw<JobRow>(
            """
            SELECT i.Id, i.WorkUnitKey, i.Payload, i.Status, i.Attempts, i.Error, i.CreatedAt, i.CompletedAt,
                   CAST(CASE WHEN i.Status IN (0, 1) AND EXISTS (
                       SELECT 1 FROM dbo.WorkUnitLeases AS l WHERE l.WorkUnitKey = i.WorkUnitKey AND l.ExpiresAt >= @now)
                   THEN 1 ELSE 0 END AS bit) AS IsLeased
            FROM dbo.WorkItems AS i
            WHERE i.WorkspaceId = @workspace AND i.TaskType = @deletion
            """,
            SqlParameters.Long("@workspace", workspaceId),
            SqlParameters.Byte("@deletion", (byte)WorkTaskType.Deletion),
            SqlParameters.Time("@now", runtime.Clock.GetUtcNow()));
        return await Paging.ToPageAsync(
            rows,
            page,
            (query, reverse) => reverse ? query.OrderByDescending(row => row.Id) : query.OrderBy(row => row.Id),
            row => row.ToRecord(workspaceName),
            ct);
    }

    /// <summary>
    /// Ends every other lease on a unit with open items in the target: its token moves on and the unit is free, so its
    /// holder's next fence fails. One statement per lease row, in a fixed order (see <see cref="CancelOtherWorkAsync"/>).
    /// The units come from a snapshot read; no work can be added under the target once it is being deleted.
    /// </summary>
    private static async Task FenceOtherLeasesAsync(NachosDbContext db, WorkLease lease, DeletionScope scope, CancellationToken ct)
    {
        var units = await db.Database
            .SqlQueryRaw<OpenUnit>(
                string.Concat(
                    "SELECT i.WorkUnitKey, CAST(MAX(CASE WHEN i.TaskType = @deletion THEN 1 ELSE 0 END) AS bit) AS IsDeletion FROM dbo.WorkItems AS i WHERE ",
                    ItemsOf(scope, "i"),
                    " AND i.Status IN (0, 1) GROUP BY i.WorkUnitKey"),
                [.. ScopeParameters(scope), SqlParameters.Byte("@deletion", (byte)WorkTaskType.Deletion)])
            .ToListAsync(ct);
        var others = units
            .Where(unit => !string.Equals(unit.WorkUnitKey, lease.WorkUnitKey, StringComparison.Ordinal))
            .OrderByDescending(unit => unit.IsDeletion)
            .ThenBy(unit => unit.WorkUnitKey, StringComparer.Ordinal);
        foreach (var unit in others)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE dbo.WorkUnitLeases SET FencingToken = FencingToken + 1, ExpiresAt = @released WHERE WorkUnitKey = @key",
                [SqlParameters.Time("@released", DateTimeOffset.MinValue), SqlParameters.Text("@key", unit.WorkUnitKey)],
                ct);
        }
    }

    /// <summary>
    /// Deletes up to <paramref name="budget"/> of the target's rows, step by step in the purge order, and says whether any
    /// step still has rows. The caller holds the fence and the target's parent locks.
    /// </summary>
    private async Task<bool> PurgeAsync(NachosDbContext db, WorkLease lease, DeletionScope scope, int budget, CancellationToken ct)
    {
        var sql = scope.SessionId is null ? WorkspacePurge : SessionPurge;
        var more = await db.Database
            .SqlQueryRaw<bool>(
                sql,
                [
                    .. ScopeParameters(scope),
                    SqlParameters.Int("@batch", budget),
                    SqlParameters.Text("@own", lease.WorkUnitKey),
                    SqlParameters.Time("@now", runtime.Clock.GetUtcNow()),
                ])
            .ToListAsync(ct);
        return more.Single();
    }

    /// <summary>The predicate on <c>dbo.WorkItems</c> (as <paramref name="alias"/>) of the items under the target.</summary>
    private static string ItemsOf(DeletionScope scope, string alias) => scope.SessionId is null
        ? $"{alias}.WorkspaceId = @workspace"
        : $"{alias}.WorkspaceId = @workspace AND {alias}.SessionId = @session";

    private static SqlParameter[] ScopeParameters(DeletionScope scope) => scope.SessionId is { } sessionId
        ? [SqlParameters.Long("@workspace", scope.WorkspaceId), SqlParameters.Long("@session", sessionId)]
        : [SqlParameters.Long("@workspace", scope.WorkspaceId)];

    /// <summary>
    /// A session's purge order: message chunks, summaries, its explicit conclusions (each batch's removed ones then
    /// tombstone their dependents, D7), its work items other than the job's own, memberships, messages.
    /// </summary>
    private static readonly PurgeStep[] SessionSteps =
    [
        PurgeStep.Rows("dbo.MessageEmbeddings", "r.WorkspaceId = @workspace AND r.SessionId = @session"),
        PurgeStep.Rows("dbo.SessionSummaries", "r.SessionId = @session"),
        PurgeStep.Conclusions("r.WorkspaceId = @workspace AND r.SessionId = @session", TombstoneDependents),
        PurgeStep.Rows("dbo.WorkItems", "r.WorkspaceId = @workspace AND r.SessionId = @session AND " + NotOwnUnit, " WITH (FORCESEEK)"),
        PurgeStep.Rows("dbo.SessionPeers", "r.WorkspaceId = @workspace AND r.SessionId = @session"),
        PurgeStep.Rows("dbo.Messages", "r.SessionId = @session"),
    ];

    /// <summary>
    /// A workspace's purge order: the rows of all its sessions table by table (chunks, summaries, then every conclusion of
    /// the workspace with its source rows, work items other than the job's own, memberships, messages, the sessions), then
    /// collections, peer cards, idempotency records, its grants and its peers. No conclusion is tombstoned: all of them go.
    /// </summary>
    private static readonly PurgeStep[] WorkspaceSteps =
    [
        PurgeStep.Rows("dbo.MessageEmbeddings", "r.WorkspaceId = @workspace"),
        PurgeStep.Rows("dbo.SessionSummaries", "r.WorkspaceId = @workspace"),
        PurgeStep.Conclusions("r.WorkspaceId = @workspace", afterDelete: ""),
        PurgeStep.Rows("dbo.WorkItems", "r.WorkspaceId = @workspace AND " + NotOwnUnit, " WITH (FORCESEEK)"),
        PurgeStep.Rows("dbo.SessionPeers", "r.WorkspaceId = @workspace"),
        PurgeStep.Rows("dbo.Messages", "r.WorkspaceId = @workspace"),
        PurgeStep.Rows("dbo.Sessions", "r.WorkspaceId = @workspace"),
        PurgeStep.Rows("dbo.Collections", "r.WorkspaceId = @workspace"),
        PurgeStep.Rows("dbo.PeerCards", "r.WorkspaceId = @workspace"),
        PurgeStep.Rows("dbo.IdempotencyRecords", "r.WorkspaceId = @workspace"),
        PurgeStep.Rows("dbo.PrincipalGrants", "r.WorkspaceId = @workspace"),
        PurgeStep.Rows("dbo.Peers", "r.WorkspaceId = @workspace"),
    ];

    private static readonly string SessionPurge = PurgeStep.Batch(SessionSteps);

    private static readonly string WorkspacePurge = PurgeStep.Batch(WorkspaceSteps);

    /// <summary>Every work item of the target but those of the job's own unit, compared exactly (<c>=</c> ignores trailing spaces).</summary>
    private const string NotOwnUnit = "NOT (r.WorkUnitKey = @own AND DATALENGTH(r.WorkUnitKey) = DATALENGTH(@own))";

    /// <summary>
    /// D7: a conclusion that cites one this batch removed (<c>@gone</c>) is tombstoned when none of its sources is still
    /// live (present and not tombstoned). Candidates come from a snapshot read; only they are updated, by key.
    /// </summary>
    /// <remarks>
    /// The candidates are first locked (X, by a no-op update in <c>Id</c> order), and only then re-evaluated by a new
    /// statement, whose snapshot starts once the locks are granted. Otherwise two jobs that each remove one of a
    /// conclusion's two sources would each still see the other's source live in their own snapshot, and neither would
    /// tombstone it. With the lock, the second job waits for the first to commit and then sees both sources gone.
    /// </remarks>
    private const string TombstoneDependents = """
        INSERT @dependents (Id) SELECT DISTINCT cs.DerivedId FROM dbo.ConclusionSources AS cs JOIN @gone AS g ON g.Id = cs.SourceId;
        UPDATE d SET DeletedAt = d.DeletedAt
        FROM dbo.Conclusions AS d WITH (FORCESEEK) JOIN @dependents AS x ON x.Id = d.Id
        WHERE d.DeletedAt IS NULL;
        UPDATE d SET DeletedAt = @now
        FROM dbo.Conclusions AS d WITH (FORCESEEK) JOIN @dependents AS x ON x.Id = d.Id
        WHERE d.DeletedAt IS NULL AND NOT EXISTS (
            SELECT 1 FROM dbo.ConclusionSources AS cs JOIN dbo.Conclusions AS src ON src.Id = cs.SourceId
            WHERE cs.DerivedId = d.Id AND src.WorkspaceId = d.WorkspaceId AND src.DeletedAt IS NULL);
        """;

    /// <summary>Inserts the target's deletion item, available now, and returns its id.</summary>
    private async Task<long> EnqueueAsync(
        NachosDbContext db, long workspaceId, long? sessionId, DeletionTarget target, CancellationToken ct) =>
        (await db.Database
            .SqlQueryRaw<long>(
                """
                INSERT dbo.WorkItems (WorkspaceId, TaskType, WorkUnitKey, Payload, Status, Attempts, AvailableAt, TokenCount, SessionId, CreatedAt)
                OUTPUT inserted.Id AS [Value]
                VALUES (@workspace, @deletion, @key, @payload, 0, 0, @now, 0, @session, @now)
                """,
                SqlParameters.Long("@workspace", workspaceId),
                SqlParameters.Byte("@deletion", (byte)WorkTaskType.Deletion),
                SqlParameters.Text("@key", target.WorkUnitKey),
                SqlParameters.LongText("@payload", DeletionPayload.Create(target).ToJsonString()),
                SqlParameters.Time("@now", runtime.Clock.GetUtcNow()),
                SqlParameters.OptionalLong("@session", sessionId))
            .ToListAsync(ct))
        .Single();

    /// <summary>Marks the target row deleting by its job; <c>table</c> is a fixed name chosen by the caller, never user input.</summary>
    private static Task<int> MarkDeletingAsync(NachosDbContext db, string table, long id, long jobId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            string.Concat("UPDATE ", table, " SET LifecycleState = @deleting, DeletionJobId = @job WHERE Id = @id"),
            [
                SqlParameters.Byte("@deleting", WriteAdmission.Deleting),
                SqlParameters.Long("@job", jobId),
                SqlParameters.Long("@id", id),
            ],
            ct);

    private Task PauseBeforeTransitionAsync(CancellationToken ct) =>
        runtime.BeforeLifecycleTransition?.Invoke(ct) ?? Task.CompletedTask;

    /// <summary>The target row as the acceptance's <c>UPDATE</c> outputs it.</summary>
    private sealed record LifecycleRow(long Id, byte LifecycleState, long? DeletionJobId)
    {
        /// <summary>The existing job when the target is already being deleted; null otherwise.</summary>
        public DeletionAcceptance? AlreadyAccepted()
        {
            if (LifecycleState != WriteAdmission.Deleting)
            {
                return null;
            }

            if (DeletionJobId is not { } jobId)
            {
                throw new InvalidOperationException($"Row {Id} is being deleted but records no deletion job.");
            }

            return new DeletionAcceptance(jobId, AlreadyDeleting: true);
        }
    }

    /// <summary>A unit with open items under the target; <c>IsDeletion</c> when one of them is a deletion.</summary>
    private sealed record OpenUnit(string WorkUnitKey, bool IsDeletion);

    /// <summary>
    /// One step of the purge: its <c>DELETE</c>, which runs while the batch's row budget <c>@left</c> is positive and spends
    /// it, and its <c>EXISTS</c> test for rows left. Tables and predicates are constants of this class, never input.
    /// </summary>
    private sealed record PurgeStep(string Delete, string Remaining)
    {
        public static PurgeStep Rows(string table, string predicate, string hints = "") => new(
            $"""
            IF @left > 0
            BEGIN
                DELETE TOP (@left) r FROM {table} AS r{hints} WHERE {predicate};
                SET @left -= @@ROWCOUNT;
            END;
            """,
            $"EXISTS (SELECT 1 FROM {table} AS r WHERE {predicate})");

        /// <summary>
        /// Conclusions, in any order (each batch re-evaluates the dependents of exactly the ones it removed), with their own
        /// source rows (which reference them); <paramref name="afterDelete"/> runs with the removed ids in <c>@gone</c>.
        /// Source rows that name a removed conclusion stay: reads leave purged sources out.
        /// </summary>
        public static PurgeStep Conclusions(string predicate, string afterDelete) => new(
            $"""
            IF @left > 0
            BEGIN
                INSERT @gone (Id) SELECT TOP (@left) r.Id FROM dbo.Conclusions AS r WHERE {predicate};
                DELETE cs FROM dbo.ConclusionSources AS cs WITH (FORCESEEK) JOIN @gone AS g ON g.Id = cs.DerivedId;
                DELETE c FROM dbo.Conclusions AS c WITH (FORCESEEK) JOIN @gone AS g ON g.Id = c.Id;
                SET @left -= @@ROWCOUNT;
                {afterDelete}
            END;
            """,
            $"EXISTS (SELECT 1 FROM dbo.Conclusions AS r WHERE {predicate})");

        /// <summary>The whole batch over <c>@batch</c> rows; its one result is whether any step has rows left.</summary>
        public static string Batch(IReadOnlyList<PurgeStep> steps) => string.Join(
            Environment.NewLine,
            [
                "DECLARE @left int = @batch;",
                "DECLARE @gone TABLE (Id bigint PRIMARY KEY);",
                "DECLARE @dependents TABLE (Id bigint PRIMARY KEY);",
                .. steps.Select(step => step.Delete),
                $"SELECT CAST(CASE WHEN {string.Join(" OR ", steps.Select(step => step.Remaining))} THEN 1 ELSE 0 END AS bit) AS [Value];",
            ]);
    }

    private sealed record JobRow(
        long Id,
        string WorkUnitKey,
        string Payload,
        byte Status,
        int Attempts,
        string? Error,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt,
        bool IsLeased)
    {
        public JobRecord ToRecord(string workspaceName)
        {
            var item = new WorkItemRecord(
                Id, workspaceName, WorkTaskType.Deletion, WorkUnitKey, SqlJson.FromStorage(Payload), Attempts, 0, null, null, CreatedAt);
            var status = Status switch
            {
                2 => JobStatus.Completed,
                3 => JobStatus.DeadLettered,
                _ when IsLeased => JobStatus.InProgress,
                _ => JobStatus.Pending,
            };
            return new JobRecord(Id, WorkTaskType.Deletion, DeletionPayload.Read(item), status, Attempts, Error, CreatedAt, CompletedAt);
        }
    }
}
