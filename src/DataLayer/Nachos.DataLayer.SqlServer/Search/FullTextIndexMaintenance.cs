using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nachos.Abstractions.Domain;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.DataLayer.SqlServer.Storage;
using Nachos.DataLayer.SqlServer.Stores;

namespace Nachos.DataLayer.SqlServer.Search;

/// <summary>
/// Creates the full-text index on <c>dbo.Messages(Content)</c> at runtime, where the server has Full-Text Search, and
/// reports when it is ready (<see cref="IsReady"/>); until then lexical search uses <c>LIKE</c>. The index lives outside
/// the dacpac so the dacpac deploys where Full-Text Search is not installed (plan divergence D1).
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="SqlServerOptions.FullTextIndexPollInterval"/>, after the schema gate passes, one check: when
/// <c>SERVERPROPERTY('IsFullTextInstalled')</c> is 1 (NULL counts as not installed), the key index <c>PK_Messages</c> is
/// usable as a full-text key, and the index is missing, the replica holding the <see cref="LeaseKey"/> lease creates the
/// catalog <see cref="CatalogName"/> and the index (<c>CHANGE_TRACKING AUTO</c>). Both statements check for the object
/// first, so a replica that takes the lease after another created them does nothing. Then <see cref="IsReady"/> is set
/// from the table's state: an active full-text index whose <c>TableFullTextPopulateStatus</c> is 0 (idle; NULL is not
/// ready). The index can go back to populating or be dropped, so readiness is re-read on every check, and search falls
/// back to <c>LIKE</c> meanwhile.
/// </para>
/// <para>
/// The DDL runs on a connection of its own that never enlists in an ambient transaction, each statement in autocommit:
/// SQL Server refuses <c>CREATE FULLTEXT INDEX</c> inside a user transaction (error 574).
/// </para>
/// <para>
/// A failed check is logged and retried at the next interval; it never stops the host.
/// </para>
/// </remarks>
public sealed partial class FullTextIndexMaintenance : BackgroundService
{
    /// <summary>The work unit whose lease one replica holds while it creates the catalog and the index.</summary>
    public const string LeaseKey = "reconciler:fts";

    public const string CatalogName = "NachosFts";

    /// <summary>Long enough for the two DDL statements; the lease is released as soon as they finish.</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly SqlServerOptions _options;
    private readonly SchemaGate _schemaGate;
    private readonly TimeProvider _clock;
    private readonly ILogger<FullTextIndexMaintenance> _logger;
    private readonly SqlWorkQueue _workQueue;
    private readonly string _owner = $"fts:{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private volatile bool _isReady;
    private FullTextState? _lastReported;

    public FullTextIndexMaintenance(
        SqlServerOptions options, SchemaGate schemaGate, TimeProvider clock, ILogger<FullTextIndexMaintenance> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schemaGate);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _schemaGate = schemaGate;
        _clock = clock;
        _logger = logger;
        _workQueue = new SqlWorkQueue(new SqlStoreRuntime(options, schemaGate, clock, logger));
    }

    /// <summary>
    /// True while the index exists and is fully populated, as of the last check: lexical search may use
    /// <c>CONTAINSTABLE</c>. False before the first check, and always where Full-Text Search is not installed.
    /// </summary>
    public bool IsReady => _isReady;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Task.Run: the first check would otherwise run inside host startup until its first await completes.
        await Task.Run(() => RunAsync(stoppingToken), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>One check: creates the index when it is missing and this replica gets the lease, then refreshes <see cref="IsReady"/>.</summary>
    internal async Task CheckAsync(CancellationToken ct)
    {
        await _schemaGate.EnsureAsync(ct).ConfigureAwait(false);
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        if (state == FullTextState.Missing && await TryCreateAsync(ct).ConfigureAwait(false))
        {
            state = await ReadStateAsync(ct).ConfigureAwait(false);
        }

        _isReady = state == FullTextState.Ready;
        Report(state);
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
                    _isReady = false;
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

    private async Task<FullTextState> ReadStateAsync(CancellationToken ct)
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
                CAST(OBJECTPROPERTYEX(OBJECT_ID(N'dbo.Messages'), 'TableHasActiveFulltextIndex') AS int),
                CAST(OBJECTPROPERTYEX(OBJECT_ID(N'dbo.Messages'), 'TableFullTextPopulateStatus') AS int)
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        int? Column(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        var (installed, usableKey, active, populateStatus) = (Column(0), Column(1), Column(2), Column(3));
        return installed != 1 ? FullTextState.NotInstalled
            : usableKey != 1 ? FullTextState.UnusableKey
            : active != 1 ? FullTextState.Missing
            : populateStatus == 0 ? FullTextState.Ready
            : FullTextState.Populating;
    }

    /// <returns>Whether this replica held the lease and ran the DDL.</returns>
    private async Task<bool> TryCreateAsync(CancellationToken ct)
    {
        if (await _workQueue.TryLeaseAsync(LeaseKey, Owner, LeaseDuration, ct).ConfigureAwait(false) is not { } lease)
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
                    CREATE FULLTEXT INDEX ON dbo.Messages (Content) KEY INDEX PK_Messages ON [{CatalogName}] WITH CHANGE_TRACKING AUTO
                """,
                ct).ConfigureAwait(false);
            LogCreated(CatalogName);
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

    /// <summary>Logs a change of state once, not on every check.</summary>
    private void Report(FullTextState state)
    {
        if (state == _lastReported)
        {
            return;
        }

        _lastReported = state;
        switch (state)
        {
            case FullTextState.NotInstalled:
                LogNotInstalled();
                break;
            case FullTextState.UnusableKey:
                LogUnusableKey();
                break;
            case FullTextState.Ready:
                LogReady();
                break;
            default:
                LogNotReady(state);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Full-Text Search is not installed on the server; lexical message search uses LIKE.")]
    private partial void LogNotInstalled();

    [LoggerMessage(Level = LogLevel.Error, Message = "The full-text index was not created: PK_Messages on dbo.Messages is not a unique, single-column, non-nullable index, which a full-text key must be. Lexical message search uses LIKE until the schema is repaired.")]
    private partial void LogUnusableKey();

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the full-text catalog {Catalog} and the full-text index on dbo.Messages(Content).")]
    private partial void LogCreated(string catalog);

    [LoggerMessage(Level = LogLevel.Information, Message = "The full-text index on dbo.Messages is ready; lexical message search uses it.")]
    private partial void LogReady();

    [LoggerMessage(Level = LogLevel.Information, Message = "The full-text index on dbo.Messages is not ready ({State}); lexical message search uses LIKE.")]
    private partial void LogNotReady(FullTextState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The full-text index check failed; lexical message search uses LIKE until a later check succeeds.")]
    private partial void LogCheckFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the full-text index lease failed; it expires on its own.")]
    private partial void LogReleaseFailed(Exception exception);

    private enum FullTextState
    {
        NotInstalled,
        UnusableKey,
        Missing,
        Populating,
        Ready,
    }
}
