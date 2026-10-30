using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Entities;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <summary>The fenced work queue over <c>dbo.WorkItems</c> and <c>dbo.WorkUnitLeases</c> (spec §10.3).</summary>
/// <remarks>
/// <para>
/// <b>Leases.</b> A unit's <c>WorkUnitLeases</c> row is never deleted, so its <c>FencingToken</c> only grows. A lease is
/// held while the row carries its owner and token. Completing or failing releases it by moving the token past the lease's
/// and the expiry to <see cref="DateTimeOffset.MinValue"/>: the released lease no longer matches, and the unit is free.
/// Expired means the clock is strictly past <c>ExpiresAt</c>; an expired row keeps proving its lease until another owner
/// takes it with the single-statement reclaim <c>UPDATE … SET FencingToken = FencingToken + 1 … WHERE ExpiresAt &lt; @now</c>.
/// </para>
/// <para>
/// <b>Claim</b>, normative (spec §10.3), under <c>READ COMMITTED</c> with read committed snapshot ON:
/// (1) a snapshot read picks up to <see cref="Candidates"/> due units, oldest lowest open item first, skipping units under
/// an unexpired lease; then, per candidate, in its own transaction: (2) its lowest open item is locked
/// <c>WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)</c> (never <c>ROWLOCK</c> with <c>READCOMMITTEDLOCK</c>), so a concurrent
/// claimer of the same unit skips it at once; (3) the lease is taken by the reclaim above
/// (<c>WITH (READPAST, READCOMMITTEDLOCK)</c>, so it skips a lease row a committing holder has locked) or, when the
/// snapshot shows no lease row at all, an insert (a 2627/2601 unique violation means another claimer won: next
/// candidate); (4) holding the lease, the unit's open items are read again (locking, no <c>READPAST</c>, so the batch is
/// exact), due-ness and the batch are decided, and the batch's <c>Attempts</c> are incremented. Any step that finds
/// nothing rolls back, leaving no trace.
/// </para>
/// <para>
/// <b>Lock order</b> of every write under a lease (complete, fail, <see cref="SqlConclusionStore.CommitDerivationAsync"/>):
/// the lease row first (the fence <c>EXISTS … WITH (UPDLOCK)</c>, held to commit, so no reclaim can interleave), then
/// work items, then collections, then conclusions. A claim takes an item before the lease row but never waits for the
/// lease row, so the two orders cannot form a cycle. Every item read and update seeks (<c>FORCESEEK</c>): a scan would
/// lock other units' rows and deadlock with their claimers. A deadlock victim of anything else is skipped like a lost race.
/// </para>
/// </remarks>
internal sealed partial class SqlWorkQueue(SqlStoreRuntime runtime) : IWorkQueue
{
    /// <summary>How many due units one claim tries, in order, before reporting nothing due.</summary>
    private const int Candidates = 32;

    private const int MaxErrorLength = 4000;

    public async Task<WorkLease?> TryAcquireAsync(
        string owner,
        IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        RequireOwner(owner);
        ArgumentNullException.ThrowIfNull(taskTypes);
        if (taskTypes.Count == 0)
        {
            return null;
        }

        List<Candidate> candidates;
        await using (var db = await runtime.OpenAsync(ct))
        {
            candidates = await FindDueUnitsAsync(db, taskTypes, runtime.Clock.GetUtcNow(), ct);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                // The clock is read per candidate, so a lease granted after earlier attempts is as long as requested.
                if (await TryClaimAsync(candidate, owner, taskTypes, leaseDuration, runtime.Clock.GetUtcNow(), ct) is { } lease)
                {
                    return lease;
                }
            }
            catch (Exception ex) when (SqlErrors.IsDeadlockVictim(ex))
            {
                // The claim's transaction was rolled back, so nothing changed: the unit is skipped like a lost race.
                LogClaimDeadlock(runtime.Logger, candidate.WorkUnitKey, ex);
            }
        }

        return null;
    }

    public async Task<WorkLease?> TryLeaseAsync(string workUnitKey, string owner, TimeSpan leaseDuration, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(workUnitKey);
        if (workUnitKey.Length > WorkUnitKeys.MaxLength)
        {
            throw new ArgumentException(
                $"A work-unit key must be at most {WorkUnitKeys.MaxLength} characters; this one has {workUnitKey.Length}.",
                nameof(workUnitKey));
        }

        RequireOwner(owner);
        var now = runtime.Clock.GetUtcNow();
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (await TakeLeaseAsync(db, workUnitKey, owner, Clamp.Add(now, leaseDuration), now, ct) is not { } token)
        {
            return null;
        }

        await transaction.CommitAsync(ct);
        return new WorkLease(workUnitKey, owner, token, []);
    }

    public async Task RenewAsync(WorkLease lease, TimeSpan leaseDuration, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        await using var db = await runtime.OpenAsync(ct);
        var renewed = await db.Database.ExecuteSqlRawAsync(
            $"UPDATE dbo.WorkUnitLeases SET ExpiresAt = @expiresAt WHERE {HeldBy}",
            [SqlParameters.Time("@expiresAt", Clamp.Add(runtime.Clock.GetUtcNow(), leaseDuration)), .. LeaseKeys(lease)],
            ct);
        if (renewed == 0)
        {
            throw new LeaseLostException(lease.WorkUnitKey);
        }
    }

    public async Task CompleteAsync(WorkLease lease, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await RequireHeldAsync(db, lease, ct);
        await CompleteItemsAsync(db, lease, runtime.Clock.GetUtcNow(), ct);
        await FreeLeaseAsync(db, lease, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ReleaseAsync(WorkLease lease, TimeSpan retryDelay, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        var availableAt = Clamp.Add(runtime.Clock.GetUtcNow(), retryDelay);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await RequireHeldAsync(db, lease, ct);
        if (lease.Items.Count > 0)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE i SET Status = 0, Attempts = CASE WHEN i.Attempts > 0 THEN i.Attempts - 1 ELSE 0 END, AvailableAt = @availableAt
                FROM dbo.WorkItems AS i WITH (FORCESEEK)
                JOIN OPENJSON(@ids) WITH (Id bigint '$') AS j ON i.Id = j.Id
                WHERE i.WorkUnitKey = @key AND i.Status IN (0, 1)
                """,
                [
                    SqlParameters.Time("@availableAt", availableAt),
                    SqlParameters.LongText("@ids", ItemIds(lease)),
                    SqlParameters.Text("@key", lease.WorkUnitKey),
                ],
                ct);
        }

        await FreeLeaseAsync(db, lease, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task FailAsync(WorkLease lease, string reason, TimeSpan retryDelay, int maxAttempts, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        var now = runtime.Clock.GetUtcNow();
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await RequireHeldAsync(db, lease, ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE i
            SET Error = @error,
                Status = CASE WHEN i.Attempts >= @maxAttempts THEN 3 ELSE 1 END,
                CompletedAt = CASE WHEN i.Attempts >= @maxAttempts THEN @now ELSE NULL END,
                AvailableAt = CASE WHEN i.Attempts >= @maxAttempts THEN i.AvailableAt ELSE @retryAt END
            FROM dbo.WorkItems AS i WITH (FORCESEEK)
            JOIN OPENJSON(@ids) WITH (Id bigint '$') AS j ON i.Id = j.Id
            WHERE i.WorkUnitKey = @key AND i.Status IN (0, 1)
            """,
            [
                SqlParameters.Text("@error", reason.Length > MaxErrorLength ? reason[..MaxErrorLength] : reason),
                SqlParameters.Int("@maxAttempts", maxAttempts),
                SqlParameters.Time("@now", now),
                SqlParameters.Time("@retryAt", Clamp.Add(now, retryDelay)),
                SqlParameters.LongText("@ids", ItemIds(lease)),
                SqlParameters.Text("@key", lease.WorkUnitKey),
            ],
            ct);
        await FreeLeaseAsync(db, lease, ct);
        await transaction.CommitAsync(ct);
    }

    /// <remarks>
    /// One snapshot statement. An open item is in progress while its unit's lease is unexpired, else pending; items are
    /// counted per session name, and the totals include items of no session.
    /// </remarks>
    public async Task<QueueStatusRecord> GetStatusAsync(
        string workspaceName, string? observerName, string? senderName, string? sessionName, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);

        var conditions = new List<string> { "i.WorkspaceId = @workspace" };
        var parameters = new List<SqlParameter>
        {
            SqlParameters.Long("@workspace", workspaceId),
            SqlParameters.Time("@now", runtime.Clock.GetUtcNow()),
        };
        if (observerName is not null)
        {
            conditions.Add(
                "EXISTS (SELECT 1 FROM OPENJSON(i.Payload, '$.observers') AS o WHERE o.[value] COLLATE Latin1_General_100_BIN2 = @observer AND DATALENGTH(o.[value]) = DATALENGTH(@observer))");
            parameters.Add(SqlParameters.Text("@observer", observerName));
        }

        if (senderName is not null)
        {
            conditions.Add(
                "EXISTS (SELECT 1 FROM dbo.Messages AS m JOIN dbo.Peers AS p ON p.Id = m.PeerId WHERE m.Id = i.MessageId AND p.Name = @sender AND DATALENGTH(p.Name) = DATALENGTH(@sender))");
            parameters.Add(SqlParameters.Text("@sender", senderName));
        }

        if (sessionName is not null)
        {
            conditions.Add("s.Name = @session AND DATALENGTH(s.Name) = DATALENGTH(@session)");
            parameters.Add(SqlParameters.Text("@session", sessionName));
        }

        var rows = await db.Database
            .SqlQueryRaw<StatusRow>(
                string.Concat(
                    """
                    SELECT s.Name AS SessionName,
                           SUM(CASE WHEN i.Status IN (0, 1) AND l.WorkUnitKey IS NULL THEN 1 ELSE 0 END) AS Pending,
                           SUM(CASE WHEN i.Status IN (0, 1) AND l.WorkUnitKey IS NOT NULL THEN 1 ELSE 0 END) AS InProgress,
                           SUM(CASE WHEN i.Status = 2 THEN 1 ELSE 0 END) AS Completed,
                           SUM(CASE WHEN i.Status = 3 THEN 1 ELSE 0 END) AS DeadLettered
                    FROM dbo.WorkItems AS i
                    LEFT JOIN dbo.Sessions AS s ON s.Id = i.SessionId
                    LEFT JOIN dbo.WorkUnitLeases AS l ON l.WorkUnitKey = i.WorkUnitKey AND l.ExpiresAt >= @now
                    WHERE
                    """,
                    " ",
                    string.Join(" AND ", conditions),
                    " GROUP BY s.Name"),
                [.. parameters])
            .ToListAsync(ct);

        var totals = new QueueCounts(
            rows.Sum(row => (long)row.Pending),
            rows.Sum(row => (long)row.InProgress),
            rows.Sum(row => (long)row.Completed),
            rows.Sum(row => (long)row.DeadLettered));
        var sessions = rows
            .Where(row => row.SessionName is not null)
            .ToDictionary(row => row.SessionName!, row => row.ToCounts(), StringComparer.Ordinal);
        return new QueueStatusRecord(totals, sessions);
    }

    /// <remarks>
    /// In batches of <see cref="SqlConclusionStore.PurgeBatch"/>, each its own statement, so the delete never escalates to
    /// a table lock that would block appends and claims.
    /// </remarks>
    public async Task<int> PruneSucceededAsync(TimeSpan retention, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        var cutoff = Clamp.Subtract(runtime.Clock.GetUtcNow(), retention);
        await using var db = await runtime.OpenAsync(ct);
        var total = 0;
        while (true)
        {
            var pruned = await db.Database.ExecuteSqlRawAsync(
                "DELETE TOP (@size) dbo.WorkItems WHERE Status = 2 AND CompletedAt <= @cutoff",
                [SqlParameters.Int("@size", SqlConclusionStore.PurgeBatch), SqlParameters.Time("@cutoff", cutoff)],
                ct);
            total += pruned;
            if (pruned < SqlConclusionStore.PurgeBatch)
            {
                return total;
            }
        }
    }

    /// <remarks>One snapshot statement; due-ness is the claim's own (<see cref="DueUnitsQuery"/>).</remarks>
    public async Task<QueueBacklog> GetBacklogAsync(IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(taskTypes);
        if (taskTypes.Count == 0)
        {
            return new QueueBacklog(0, null);
        }

        var (cte, parameters) = DueUnitsQuery(taskTypes, runtime.Clock.GetUtcNow());
        var sql = $"""
            {cte}
            SELECT (SELECT COUNT_BIG(*) FROM due) AS EligibleUnits,
                   (SELECT MIN(i.CreatedAt) FROM dbo.WorkItems AS i
                    WHERE i.Status IN (0, 1) AND i.TaskType IN (SELECT TaskType FROM policy)) AS OldestOpenItemCreatedAt
            """;
        await using var db = await runtime.OpenAsync(ct);
        var row = (await db.Database.SqlQueryRaw<BacklogRow>(sql, [.. parameters]).ToListAsync(ct)).Single();
        return new QueueBacklog(row.EligibleUnits, row.OldestOpenItemCreatedAt);
    }

    /// <summary>
    /// The fence of every write under a lease: the lease row still carries the lease's owner and token. Its update lock is
    /// held until the caller's transaction ends, so the lease cannot be reclaimed before the caller commits.
    /// </summary>
    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    internal static async Task RequireHeldAsync(NachosDbContext db, WorkLease lease, CancellationToken ct)
    {
        var held = await db.Database
            .SqlQueryRaw<int>(
                $"SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.WorkUnitLeases WITH (UPDLOCK) WHERE {HeldBy}) THEN 1 ELSE 0 END AS [Value]",
                [.. LeaseKeys(lease)])
            .ToListAsync(ct);
        if (held.Single() == 0)
        {
            throw new LeaseLostException(lease.WorkUnitKey);
        }
    }

    /// <summary>Marks the lease's items succeeded. The caller holds the lease (<see cref="RequireHeldAsync"/>).</summary>
    internal static Task CompleteItemsAsync(NachosDbContext db, WorkLease lease, DateTimeOffset now, CancellationToken ct) =>
        lease.Items.Count == 0
            ? Task.CompletedTask
            : db.Database.ExecuteSqlRawAsync(
                """
                UPDATE i SET Status = 2, CompletedAt = @now
                FROM dbo.WorkItems AS i WITH (FORCESEEK)
                JOIN OPENJSON(@ids) WITH (Id bigint '$') AS j ON i.Id = j.Id
                WHERE i.WorkUnitKey = @key AND i.Status IN (0, 1)
                """,
                [SqlParameters.Time("@now", now), SqlParameters.LongText("@ids", ItemIds(lease)), SqlParameters.Text("@key", lease.WorkUnitKey)],
                ct);

    /// <summary>Releases a held lease: its token no longer matches and the unit is free. The row stays, so tokens keep growing.</summary>
    internal static Task FreeLeaseAsync(NachosDbContext db, WorkLease lease, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.WorkUnitLeases SET FencingToken = FencingToken + 1, ExpiresAt = @released WHERE WorkUnitKey = @key",
            [SqlParameters.Time("@released", DateTimeOffset.MinValue), SqlParameters.Text("@key", lease.WorkUnitKey)],
            ct);

    /// <summary>
    /// The predicate of a held lease over <see cref="LeaseKeys"/>. <c>Owner</c> uses the database collation, so it is
    /// compared under a binary one, with <c>DATALENGTH</c> because <c>=</c> ignores trailing spaces.
    /// </summary>
    private const string HeldBy =
        "WorkUnitKey = @key AND FencingToken = @token AND Owner COLLATE Latin1_General_100_BIN2 = @owner AND DATALENGTH(Owner) = DATALENGTH(@owner)";

    private static SqlParameter[] LeaseKeys(WorkLease lease) =>
    [
        SqlParameters.Text("@key", lease.WorkUnitKey),
        SqlParameters.Long("@token", lease.FencingToken),
        SqlParameters.Text("@owner", lease.Owner),
    ];

    /// <summary>
    /// The lease's item ids, ascending, as the JSON array the item updates join on. Those updates seek
    /// (<c>FORCESEEK</c>): a scan would lock rows of other units, which claimers and appenders hold, and deadlock.
    /// </summary>
    private static string ItemIds(WorkLease lease) => JsonSerializer.Serialize(lease.Items.Select(item => item.Id).Order());

    /// <summary>
    /// Step 1 of a claim: up to <see cref="Candidates"/> due units (<see cref="DueUnitsQuery"/>), as of one snapshot,
    /// oldest lowest open item first.
    /// </summary>
    private static async Task<List<Candidate>> FindDueUnitsAsync(
        NachosDbContext db, IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, DateTimeOffset now, CancellationToken ct)
    {
        var (cte, parameters) = DueUnitsQuery(taskTypes, now);
        parameters.Add(SqlParameters.Int("@candidates", Candidates));
        var sql = $"""
            {cte}
            SELECT TOP (@candidates) d.WorkUnitKey, d.FirstId
            FROM due AS d
            ORDER BY d.FirstId
            """;
        return await db.Database.SqlQueryRaw<Candidate>(sql, [.. parameters]).ToListAsync(ct);
    }

    /// <summary>
    /// The common table expressions ending in <c>due</c>: the due units, each with its lowest open item (<c>FirstId</c>).
    /// A unit's claimable items are its open items of the requested types below the first one still backing off; it is
    /// due when no unexpired lease holds it and its claimable items hold enough tokens or the oldest is old enough, by the
    /// policy of its lowest open item's type. <c>policy</c> lists the requested types.
    /// </summary>
    private static (string Cte, List<SqlParameter> Parameters) DueUnitsQuery(
        IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, DateTimeOffset now)
    {
        var policies = new List<string>(taskTypes.Count);
        var parameters = new List<SqlParameter> { SqlParameters.Time("@now", now) };
        foreach (var ((type, policy), i) in taskTypes.Select((pair, i) => (pair, i)))
        {
            policies.Add($"(@type{i}, @tokens{i}, @cutoff{i})");
            parameters.Add(SqlParameters.Byte($"@type{i}", (byte)type));
            parameters.Add(SqlParameters.Int($"@tokens{i}", policy.EligibilityTokens));
            parameters.Add(SqlParameters.Time($"@cutoff{i}", Clamp.Subtract(now, policy.MaxAge)));
        }

        var cte = $"""
            WITH policy (TaskType, EligibilityTokens, AgeCutoff) AS (SELECT * FROM (VALUES {string.Join(", ", policies)}) AS p (TaskType, EligibilityTokens, AgeCutoff)),
            open_items AS (
                SELECT i.Id, i.WorkUnitKey, i.TaskType, i.AvailableAt, i.TokenCount, i.CreatedAt
                FROM dbo.WorkItems AS i
                WHERE i.Status IN (0, 1)
                  AND i.TaskType IN (SELECT TaskType FROM policy)
                  AND NOT EXISTS (SELECT 1 FROM dbo.WorkUnitLeases AS l WHERE l.WorkUnitKey = i.WorkUnitKey AND l.ExpiresAt >= @now)),
            units AS (
                SELECT WorkUnitKey, MIN(Id) AS FirstId, MIN(CASE WHEN AvailableAt > @now THEN Id END) AS BlockedId
                FROM open_items GROUP BY WorkUnitKey),
            claimable AS (
                SELECT u.WorkUnitKey, u.FirstId, SUM(CAST(o.TokenCount AS bigint)) AS Tokens, MIN(o.CreatedAt) AS Oldest
                FROM units AS u JOIN open_items AS o ON o.WorkUnitKey = u.WorkUnitKey
                WHERE u.BlockedId IS NULL OR o.Id < u.BlockedId
                GROUP BY u.WorkUnitKey, u.FirstId),
            due AS (
                SELECT c.WorkUnitKey, c.FirstId
                FROM claimable AS c
                JOIN open_items AS f ON f.Id = c.FirstId
                JOIN policy AS p ON p.TaskType = f.TaskType
                WHERE c.Tokens >= p.EligibilityTokens OR c.Oldest <= p.AgeCutoff)
            """;
        return (cte, parameters);
    }

    /// <summary>Steps 2 to 4 of a claim on one candidate, in one transaction; null (and nothing changed) when it is not claimable.</summary>
    private async Task<WorkLease?> TryClaimAsync(
        Candidate candidate,
        string owner,
        IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var locked = await db.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS [Value] FROM dbo.WorkItems WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK) WHERE Id = @first AND Status IN (0, 1)",
                SqlParameters.Long("@first", candidate.FirstId))
            .ToListAsync(ct);
        if (locked.Single() == 0
            || await TakeLeaseAsync(db, candidate.WorkUnitKey, owner, Clamp.Add(now, leaseDuration), now, ct) is not { } token)
        {
            return null;
        }

        var types = string.Join(", ", taskTypes.Keys.Select(type => ((byte)type).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var sql = string.Concat(
            """
            SELECT i.Id, w.Name AS WorkspaceName, i.TaskType, i.Payload, i.Attempts, i.AvailableAt, i.TokenCount,
                   s.Name AS SessionName, m.PublicId AS MessageId, i.CreatedAt
            FROM dbo.WorkItems AS i WITH (UPDLOCK, READCOMMITTEDLOCK, FORCESEEK)
            LEFT JOIN dbo.Workspaces AS w ON w.Id = i.WorkspaceId
            LEFT JOIN dbo.Sessions AS s ON s.Id = i.SessionId
            LEFT JOIN dbo.Messages AS m ON m.Id = i.MessageId
            WHERE i.WorkUnitKey = @key AND i.Status IN (0, 1) AND i.TaskType IN (
            """,
            types,
            ") ORDER BY i.Id");
        var open = await db.Database
            .SqlQueryRaw<WorkItemRow>(sql, SqlParameters.Text("@key", candidate.WorkUnitKey))
            .ToListAsync(ct);
        if (Batch(open, taskTypes, now) is not { } batch)
        {
            return null;
        }

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE i SET Attempts = i.Attempts + 1 FROM dbo.WorkItems AS i WITH (FORCESEEK) JOIN OPENJSON(@ids) WITH (Id bigint '$') AS j ON i.Id = j.Id",
            [SqlParameters.LongText("@ids", JsonSerializer.Serialize(batch.Select(item => item.Id)))],
            ct);
        await transaction.CommitAsync(ct);

        return new WorkLease(
            candidate.WorkUnitKey,
            owner,
            token,
            [
                .. batch.Select(item => new WorkItemRecord(
                    item.Id,
                    item.WorkspaceName,
                    (WorkTaskType)item.TaskType,
                    candidate.WorkUnitKey,
                    SqlJson.FromStorage(item.Payload),
                    item.Attempts + 1,
                    item.TokenCount,
                    item.SessionName,
                    item.MessageId,
                    item.CreatedAt)),
            ]);
    }

    /// <summary>
    /// The items one lease takes from a unit's open items (in <c>Id</c> order), or null when the unit is not due: the
    /// claimable prefix, if due by its lowest item's policy, cut to that policy's token target and to
    /// <see cref="WorkLease.MaxItems"/>, and always at least one.
    /// </summary>
    private static List<WorkItemRow>? Batch(
        List<WorkItemRow> open, IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, DateTimeOffset now)
    {
        var claimable = open.TakeWhile(item => item.AvailableAt <= now).ToList();
        if (claimable.Count == 0)
        {
            return null;
        }

        var policy = taskTypes[(WorkTaskType)claimable[0].TaskType];
        var isDue = claimable.Sum(item => (long)item.TokenCount) >= policy.EligibilityTokens
            || now - claimable.Min(item => item.CreatedAt) >= policy.MaxAge;
        if (!isDue)
        {
            return null;
        }

        var batch = new List<WorkItemRow> { claimable[0] };
        long tokens = claimable[0].TokenCount;
        foreach (var item in claimable.Skip(1))
        {
            tokens += item.TokenCount;
            if (tokens > policy.BatchTokenTarget || batch.Count == WorkLease.MaxItems)
            {
                break;
            }

            batch.Add(item);
        }

        return batch;
    }

    /// <summary>
    /// Takes the unit's lease inside the caller's transaction and returns its fencing token, or null when another owner
    /// holds it (or is taking it). Never waits on a lease row another transaction has locked.
    /// </summary>
    private static async Task<long?> TakeLeaseAsync(
        NachosDbContext db, string workUnitKey, string owner, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)
    {
        SqlParameter[] Parameters() =>
        [
            SqlParameters.Text("@key", workUnitKey),
            SqlParameters.Text("@owner", owner),
            SqlParameters.Time("@expiresAt", expiresAt),
            SqlParameters.Time("@now", now),
        ];

        var reclaimed = await db.Database
            .SqlQueryRaw<long>(
                """
                UPDATE dbo.WorkUnitLeases WITH (READPAST, READCOMMITTEDLOCK)
                SET Owner = @owner, ExpiresAt = @expiresAt, FencingToken = FencingToken + 1
                OUTPUT inserted.FencingToken AS [Value]
                WHERE WorkUnitKey = @key AND ExpiresAt < @now
                """,
                Parameters())
            .ToListAsync(ct);
        if (reclaimed.Count == 1)
        {
            return reclaimed[0];
        }

        // A row that exists (held, or locked by its holder's commit) is seen here in the snapshot, without waiting for it.
        var exists = await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM dbo.WorkUnitLeases WHERE WorkUnitKey = @key", Parameters())
            .ToListAsync(ct);
        if (exists.Single() > 0)
        {
            return null;
        }

        try
        {
            var inserted = await db.Database
                .SqlQueryRaw<long>(
                    """
                    INSERT dbo.WorkUnitLeases (WorkUnitKey, Owner, ExpiresAt, FencingToken)
                    OUTPUT inserted.FencingToken AS [Value]
                    VALUES (@key, @owner, @expiresAt, 1)
                    """,
                    Parameters())
                .ToListAsync(ct);
            return inserted.Single();
        }
        catch (Exception ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            return null;
        }
    }

    /// <summary>The bound of the <c>Owner nvarchar(128)</c> column, checked before the round trip.</summary>
    private static void RequireOwner(string owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Length > WorkLease.MaxOwnerLength)
        {
            throw new ArgumentException(
                $"An owner must be at most {WorkLease.MaxOwnerLength} characters; this one has {owner.Length}.", nameof(owner));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A claim of work unit {WorkUnitKey} was chosen as a deadlock victim and rolled back; the unit was skipped.")]
    private static partial void LogClaimDeadlock(ILogger logger, string workUnitKey, Exception exception);

    /// <summary>A due unit and its lowest open item.</summary>
    private sealed record Candidate(string WorkUnitKey, long FirstId);

    private sealed record BacklogRow(long EligibleUnits, DateTimeOffset? OldestOpenItemCreatedAt);

    private sealed record StatusRow(string? SessionName, int Pending, int InProgress, int Completed, int DeadLettered)
    {
        public QueueCounts ToCounts() => new(Pending, InProgress, Completed, DeadLettered);
    }
}
