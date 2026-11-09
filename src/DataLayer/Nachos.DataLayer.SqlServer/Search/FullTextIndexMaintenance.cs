using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.DataLayer.SqlServer.Stores;

namespace Nachos.DataLayer.SqlServer.Search;

/// <summary>
/// Creates the full-text index on <c>dbo.Messages(Content)</c> at runtime, where the server has Full-Text Search, and
/// keeps <see cref="FullTextIndexState.IsReady"/> current; until it is ready lexical search uses <c>LIKE</c>. The index lives
/// outside the dacpac so the dacpac deploys where Full-Text Search is not installed (plan divergence D1).
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="SqlServerOptions.FullTextIndexPollInterval"/>, after the schema gate passes, one check. When
/// <c>SERVERPROPERTY('IsFullTextInstalled')</c> is 1 (NULL counts as not installed) and the key index <c>PK_Messages</c>
/// is usable as a full-text key, the replica holding the <see cref="WorkUnitKeys.FullTextMaintenance"/> lease repairs
/// what is missing: it creates the catalog <see cref="CatalogName"/> and the index (<c>CHANGE_TRACKING AUTO</c>,
/// <c>STOPLIST OFF</c>), or, for an index an earlier build created with a stoplist, turns the stoplist off, which
/// repopulates it. Every statement checks the catalog first, so a replica that takes the lease after another did the work
/// does nothing.
/// </para>
/// <para>
/// The index is ready while it is enabled, its <c>TableFullTextPopulateStatus</c> is idle (0) or applying tracked changes
/// (3, 4), and its last crawl has completed or is an update from change tracking (<c>crawl_type</c> <c>U</c>, during which
/// <c>has_crawl_completed</c> reads 0): ordinary writes never take it out of use. A full or incremental population (1, 2,
/// or a full or incremental crawl still running, as after creation or a stoplist change), a pause (5) or a rebuild (6)
/// makes it not ready. Readiness is re-read on every check.
/// </para>
/// <para>
/// The DDL runs on a connection of its own that never enlists in an ambient transaction, each statement in autocommit:
/// SQL Server refuses <c>CREATE FULLTEXT INDEX</c> inside a user transaction (error 574). A failed check is logged and
/// retried at the next interval; it never stops the host.
/// </para>
/// </remarks>
internal sealed partial class FullTextIndexMaintenance : BackgroundService
{
    public const string CatalogName = "NachosFts";

    /// <summary>Long enough for the DDL statements; the lease is released as soon as they finish.</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly SqlServerOptions _options;
    private readonly SchemaGate _schemaGate;
    private readonly FullTextIndexState _state;
    private readonly SqlWorkQueue _workQueue;
    private readonly TimeProvider _clock;
    private readonly ILogger<FullTextIndexMaintenance> _logger;
    private readonly string _owner = $"fts:{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private IndexStatus? _lastReported;

    public FullTextIndexMaintenance(
        SqlServerOptions options,
        SchemaGate schemaGate,
        FullTextIndexState state,
        SqlWorkQueue workQueue,
        TimeProvider clock,
        ILogger<FullTextIndexMaintenance> logger)
    {
        _options = options;
        _schemaGate = schemaGate;
        _state = state;
        _workQueue = workQueue;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Whether a full-text index in this state serves queries over every message.</summary>
    internal static bool IsPopulated(bool enabled, bool crawlCompleted, string? crawlType, int? populateStatus) =>
        enabled && populateStatus is 0 or 3 or 4 && (crawlCompleted || crawlType == "U");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Task.Run: the first check would otherwise run inside host startup until its first await completes.
        await Task.Run(() => RunAsync(stoppingToken), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>One check: repairs the index when it needs it and this replica gets the lease, then refreshes the state.</summary>
    internal async Task CheckAsync(CancellationToken ct)
    {
        await _schemaGate.EnsureAsync(ct).ConfigureAwait(false);
        var status = await ReadStatusAsync(ct).ConfigureAwait(false);
        if (status is IndexStatus.Missing or IndexStatus.StoplistOn && await TryRepairAsync(ct).ConfigureAwait(false))
        {
            status = await ReadStatusAsync(ct).ConfigureAwait(false);
        }

        _state.IsReady = status == IndexStatus.Ready;
        Report(status);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _state.IsReady = false;
                    LogCheckFailed(ex);
                }

                await Task.Delay(_options.FullTextIndexPollInterval, _clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private async Task<IndexStatus> ReadStatusAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(
            """
            SELECT
                CAST(SERVERPROPERTY('IsFullTextInstalled') AS int),
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM sys.indexes AS i
                    JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                    JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.object_id = OBJECT_ID(N'dbo.Messages') AND i.name = N'PK_Messages'
                      AND i.is_unique = 1 AND i.is_disabled = 0 AND c.is_nullable = 0
                      AND (SELECT COUNT(*) FROM sys.index_columns AS k
                           WHERE k.object_id = i.object_id AND k.index_id = i.index_id AND k.is_included_column = 0) = 1)
                THEN 1 ELSE 0 END,
                CAST(fi.is_enabled AS int),
                CAST(fi.has_crawl_completed AS int),
                CASE WHEN fi.stoplist_id IS NULL THEN 0 ELSE 1 END,
                CAST(OBJECTPROPERTYEX(OBJECT_ID(N'dbo.Messages'), 'TableFullTextPopulateStatus') AS int),
                CAST(fi.crawl_type AS nvarchar(1))
            FROM (SELECT 1 AS One) AS one
            LEFT JOIN sys.fulltext_indexes AS fi ON fi.object_id = OBJECT_ID(N'dbo.Messages')
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        int? Column(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        var (installed, usableKey, enabled, crawlCompleted, hasStoplist, populateStatus) =
            (Column(0), Column(1), Column(2), Column(3), Column(4), Column(5));
        var crawlType = reader.IsDBNull(6) ? null : reader.GetString(6);
        return installed != 1 ? IndexStatus.NotInstalled
            : usableKey != 1 ? IndexStatus.UnusableKey
            : enabled is null ? IndexStatus.Missing
            : hasStoplist == 1 ? IndexStatus.StoplistOn
            : IsPopulated(enabled == 1, crawlCompleted == 1, crawlType, populateStatus) ? IndexStatus.Ready
            : IndexStatus.Populating;
    }

    /// <returns>Whether this replica held the lease and ran the DDL.</returns>
    private async Task<bool> TryRepairAsync(CancellationToken ct)
    {
        if (await _workQueue.TryLeaseAsync(WorkUnitKeys.FullTextMaintenance, Owner, LeaseDuration, ct).ConfigureAwait(false) is not { } lease)
        {
            return false;
        }

        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                $"IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'{CatalogName}') CREATE FULLTEXT CATALOG [{CatalogName}]",
                ct).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                $"""
                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Messages'))
                    CREATE FULLTEXT INDEX ON dbo.Messages (Content) KEY INDEX PK_Messages ON [{CatalogName}]
                    WITH CHANGE_TRACKING AUTO, STOPLIST OFF
                ELSE IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Messages') AND stoplist_id IS NOT NULL)
                    ALTER FULLTEXT INDEX ON dbo.Messages SET STOPLIST OFF
                """,
                ct).ConfigureAwait(false);
            LogRepaired(CatalogName);
            return true;
        }
        finally
        {
            await ReleaseAsync(lease).ConfigureAwait(false);
        }
    }

    /// <summary>Frees the unit at once; a release that fails is logged, and the lease then expires on its own.</summary>
    private async Task ReleaseAsync(WorkLease lease)
    {
        try
        {
            // CancellationToken.None: release even when stopping.
            await _workQueue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReleaseFailed(ex);
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A connection that never enlists in an ambient transaction, so every statement on it is autocommit.</summary>
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(_options.ConnectionString) { Enlist = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string Owner => _owner.Length <= WorkLease.MaxOwnerLength ? _owner : _owner[^WorkLease.MaxOwnerLength..];

    /// <summary>Logs a change of status once, not on every check.</summary>
    private void Report(IndexStatus status)
    {
        if (status == _lastReported)
        {
            return;
        }

        _lastReported = status;
        switch (status)
        {
            case IndexStatus.NotInstalled:
                LogNotInstalled();
                break;
            case IndexStatus.UnusableKey:
                LogUnusableKey();
                break;
            case IndexStatus.Ready:
                LogReady();
                break;
            default:
                LogNotReady(status);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Full-Text Search is not installed on the server; lexical message search uses LIKE.")]
    private partial void LogNotInstalled();

    [LoggerMessage(Level = LogLevel.Error, Message = "The full-text index was not created: PK_Messages on dbo.Messages is not a unique, single-column, non-nullable index, which a full-text key must be. Lexical message search uses LIKE until the schema is repaired.")]
    private partial void LogUnusableKey();

    [LoggerMessage(Level = LogLevel.Information, Message = "Created or repaired the full-text catalog {Catalog} and the full-text index on dbo.Messages(Content).")]
    private partial void LogRepaired(string catalog);

    [LoggerMessage(Level = LogLevel.Information, Message = "The full-text index on dbo.Messages is ready; lexical message search uses it.")]
    private partial void LogReady();

    [LoggerMessage(Level = LogLevel.Information, Message = "The full-text index on dbo.Messages is not ready ({Status}); lexical message search uses LIKE.")]
    private partial void LogNotReady(IndexStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The full-text index check failed; lexical message search uses LIKE until a later check succeeds.")]
    private partial void LogCheckFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the full-text index lease failed; it expires on its own.")]
    private partial void LogReleaseFailed(Exception exception);

    private enum IndexStatus
    {
        NotInstalled,
        UnusableKey,
        Missing,
        StoplistOn,
        Populating,
        Ready,
    }
}
