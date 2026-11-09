using System.Data;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <summary>
/// Deletion acceptance and job listing (spec §13.1). The deletion job's steps (<see cref="CancelOtherWorkAsync"/>,
/// <see cref="PurgeBatchAsync"/>, <see cref="RemoveAsync"/>) are not implemented yet (M3 Task 8): they throw
/// <see cref="NotSupportedException"/>.
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
/// </remarks>
internal sealed class SqlDeletionStore(SqlStoreRuntime runtime) : IDeletionStore
{
    private const string NotYet = "Not implemented by the SQL Server provider yet (M3 Task 8).";

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

    public Task CancelOtherWorkAsync(WorkLease lease, DeletionTarget target, CancellationToken ct) =>
        throw new NotSupportedException(NotYet);

    public Task<bool> PurgeBatchAsync(WorkLease lease, DeletionTarget target, int batchSize, CancellationToken ct) =>
        throw new NotSupportedException(NotYet);

    public Task RemoveAsync(WorkLease lease, DeletionTarget target, CancellationToken ct) =>
        throw new NotSupportedException(NotYet);

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
        public DeletionAcceptance? AlreadyAccepted() =>
            LifecycleState == WriteAdmission.Deleting
                ? new DeletionAcceptance(
                    DeletionJobId ?? throw new InvalidOperationException($"Row {Id} is being deleted but records no deletion job."),
                    AlreadyDeleting: true)
                : null;
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
