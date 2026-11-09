using System.Data;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <remarks>
/// One <c>dbo.SessionSummaries</c> row per (session, kind), replaced in place. <see cref="SaveAsync"/> is one transaction in
/// the lock order of <see cref="SqlWorkQueue"/>: the fence (the lease row, update-locked to commit), the workspace and
/// session rows (S, <see cref="WriteAdmission.AdmitWorkerAsync"/>), the lease's work items
/// (succeeded), the summary rows, then the lease release.
/// </remarks>
internal sealed class SqlSummaryStore(SqlStoreRuntime runtime) : ISummaryStore
{
    private const string SelectSummaries = """
        SELECT ss.Kind, ss.Content, m.PublicId AS CoversThroughMessageId, ss.CoversThroughSeq, ss.TokenCount, ss.CreatedAt
        FROM dbo.SessionSummaries AS ss
        JOIN dbo.Messages AS m ON m.Id = ss.CoversThroughMessageId
        WHERE ss.SessionId = @session
        """;

    public async Task<SummaryRecord?> GetAsync(string workspaceName, string sessionName, SummaryKind kind, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);
        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var rows = await db.Database
            .SqlQueryRaw<SummaryRow>(
                SelectSummaries + " AND ss.Kind = @kind",
                SqlParameters.Long("@session", session.SessionId),
                SqlParameters.Byte("@kind", (byte)kind))
            .ToListAsync(ct);
        return rows.SingleOrDefault()?.ToRecord();
    }

    public async Task<IReadOnlyList<SummaryRecord>> ListAsync(string workspaceName, string sessionName, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);
        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var rows = await db.Database
            .SqlQueryRaw<SummaryRow>(SelectSummaries + " ORDER BY ss.Kind", SqlParameters.Long("@session", session.SessionId))
            .ToListAsync(ct);
        return [.. rows.Select(row => row.ToRecord())];
    }

    /// <remarks>
    /// After the fence, the deletion barrier's S locks on the workspace and the session (spec §13.1) keep a concurrent
    /// deletion acceptance out until commit; under a resource being deleted or gone, nothing is written and the items
    /// complete as <see cref="SqlWorkQueue.ResourceDeleted"/>.
    /// </remarks>
    public async Task SaveAsync(
        WorkLease lease, string workspaceName, string sessionName, IReadOnlyList<NewSummary> summaries, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(workspaceName);
        ArgumentNullException.ThrowIfNull(sessionName);
        ArgumentNullException.ThrowIfNull(summaries);
        foreach (var summary in summaries)
        {
            ArgumentNullException.ThrowIfNull(summary);
            ArgumentNullException.ThrowIfNull(summary.Content);
            ArgumentNullException.ThrowIfNull(summary.CoversThroughMessageId);
        }

        if (summaries.DistinctBy(summary => summary.Kind).Count() != summaries.Count)
        {
            throw new ArgumentException("At most one summary of each kind can be saved under one lease.", nameof(summaries));
        }

        if (!string.Equals(lease.WorkUnitKey, WorkUnitKeys.Summary(workspaceName, sessionName), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The lease on '{lease.WorkUnitKey}' is not on the summary unit of session '{sessionName}'.", nameof(lease));
        }

        await using var db = await runtime.OpenAsync(ct);
        var now = runtime.Clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await SqlWorkQueue.RequireHeldAsync(db, lease, ct);
        if (!await WriteAdmission.AdmitWorkerAsync(db, workspaceName, [sessionName], [], ct))
        {
            await SqlWorkQueue.DiscardAsResourceDeletedAsync(db, transaction, lease, now, ct);
            return;
        }

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var throughIds = new List<long>(summaries.Count);
        foreach (var summary in summaries)
        {
            throughIds.Add(await FindThroughMessageAsync(db, session.SessionId, summary, ct)
                ?? throw new NotFoundException(
                    $"Message '{summary.CoversThroughMessageId}' with seq {summary.CoversThroughSeq} not found in session '{sessionName}'."));
        }

        await SqlWorkQueue.CompleteItemsAsync(db, lease, now, error: null, ct);
        foreach (var (summary, throughId) in summaries.Zip(throughIds).OrderBy(pair => pair.First.Kind))
        {
            await ReplaceIfNewerAsync(db, session.SessionId, summary, throughId, now, ct);
        }

        await SqlWorkQueue.FreeLeaseAsync(db, lease, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The row id of the session's message with the summary's public id and <c>Seq</c>, or null.</summary>
    private static async Task<long?> FindThroughMessageAsync(NachosDbContext db, long sessionId, NewSummary summary, CancellationToken ct) =>
        await db.Messages
            .Identified(sessionId, summary.CoversThroughMessageId)
            .Where(message => message.Seq == summary.CoversThroughSeq)
            .Select(message => (long?)message.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Replaces the (session, kind) row when the summary covers a greater <c>Seq</c>, or inserts it when there is none. The
    /// row's key range is update-locked first, so the insert cannot race another writer of the same kind.
    /// </summary>
    private static Task<int> ReplaceIfNewerAsync(
        NachosDbContext db, long sessionId, NewSummary summary, long throughId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            DECLARE @storedSeq bigint, @exists bit = 0;
            SELECT @storedSeq = CoversThroughSeq, @exists = 1
            FROM dbo.SessionSummaries WITH (UPDLOCK, HOLDLOCK)
            WHERE SessionId = @session AND Kind = @kind;
            IF @exists = 0
                INSERT dbo.SessionSummaries (WorkspaceId, SessionId, Kind, Content, CoversThroughMessageId, CoversThroughSeq, TokenCount, CreatedAt)
                SELECT WorkspaceId, @session, @kind, @content, @through, @seq, @tokens, @now FROM dbo.Sessions WHERE Id = @session;
            ELSE IF @seq > @storedSeq
                UPDATE dbo.SessionSummaries
                SET Content = @content, CoversThroughMessageId = @through, CoversThroughSeq = @seq, TokenCount = @tokens, CreatedAt = @now
                WHERE SessionId = @session AND Kind = @kind;
            """,
            [
                SqlParameters.Long("@session", sessionId),
                SqlParameters.Byte("@kind", (byte)summary.Kind),
                SqlParameters.LongText("@content", summary.Content),
                SqlParameters.Long("@through", throughId),
                SqlParameters.Long("@seq", summary.CoversThroughSeq),
                SqlParameters.Int("@tokens", summary.TokenCount),
                SqlParameters.Time("@now", now),
            ],
            ct);

    private sealed record SummaryRow(
        byte Kind, string Content, string CoversThroughMessageId, long CoversThroughSeq, int TokenCount, DateTimeOffset CreatedAt)
    {
        public SummaryRecord ToRecord() =>
            new((SummaryKind)Kind, Content, CoversThroughMessageId, CoversThroughSeq, TokenCount, CreatedAt);
    }
}
