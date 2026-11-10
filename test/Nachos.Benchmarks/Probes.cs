using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Nachos.Benchmarks;

/// <summary>Server-side observations: plan-cache statistics, the full-text index's state, candidate volumes and table sizes.</summary>
internal sealed class Probes(string connectionString)
{
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>Empties the plan cache, so <see cref="SlowestStatementAsync"/> sees only the series that follows.</summary>
    public Task ClearPlanCacheAsync(CancellationToken ct) => ExecuteAsync("DBCC FREEPROCCACHE WITH NO_INFOMSGS", ct);

    /// <summary>
    /// The cached statement with the most total elapsed time (the series' own search query), with per-execution averages and
    /// the index operations of its plan.
    /// </summary>
    public async Task<StatementStats?> SlowestStatementAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) qs.execution_count,
                   qs.total_elapsed_time / qs.execution_count / 1000.0,
                   qs.total_worker_time / qs.execution_count / 1000.0,
                   qs.total_logical_reads / qs.execution_count,
                   qs.total_physical_reads / qs.execution_count,
                   qs.last_dop,
                   CAST(qp.query_plan AS nvarchar(max))
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS t
            CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
            WHERE t.text LIKE N'%dbo.Messages%' AND t.text NOT LIKE N'%dm[_]exec[_]query[_]stats%'
            ORDER BY qs.total_elapsed_time DESC
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var plan = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new StatementStats(
            reader.GetInt64(0),
            (double)reader.GetDecimal(1),
            (double)reader.GetDecimal(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            plan is null ? "" : IndexOperations(plan));
    }

    /// <summary>Whether the server has Full-Text Search (<c>IsFullTextInstalled</c> = 1; NULL counts as not installed).</summary>
    public async Task<bool> IsFullTextInstalledAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("SELECT ISNULL(CAST(SERVERPROPERTY('IsFullTextInstalled') AS int), 0)", connection);
        return (int)(await command.ExecuteScalarAsync(ct))! == 1;
    }

    /// <summary>Waits until the full-text index has no pending changes and no population running.</summary>
    /// <exception cref="TimeoutException">It has not caught up within <paramref name="deadline"/>.</exception>
    public async Task WaitForFullTextAsync(TimeSpan deadline, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (clock.Elapsed > deadline)
            {
                throw new TimeoutException($"The full-text index still had pending changes after {deadline}.");
            }

            await using var connection = await OpenAsync(ct);
            await using var command = new SqlCommand(
                """
                SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('dbo.Messages'), 'TableFulltextPendingChanges') AS bigint),
                       CAST(OBJECTPROPERTYEX(OBJECT_ID('dbo.Messages'), 'TableFullTextPopulateStatus') AS int),
                       CAST(OBJECTPROPERTYEX(OBJECT_ID('dbo.Messages'), 'TableFulltextItemCount') AS bigint)
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            if (!reader.IsDBNull(0) && reader.GetInt64(0) == 0 && !reader.IsDBNull(1) && reader.GetInt32(1) == 0)
            {
                return;
            }

            Console.WriteLine($"  full-text: pending {(reader.IsDBNull(0) ? "?" : reader.GetInt64(0).ToString("N0", CultureInfo.InvariantCulture))}, " +
                $"status {(reader.IsDBNull(1) ? "?" : reader.GetInt32(1))}, items {(reader.IsDBNull(2) ? "?" : reader.GetInt64(2).ToString("N0", CultureInfo.InvariantCulture))}");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    /// <summary>How many rows <c>CONTAINSTABLE</c> returns for <paramref name="condition"/>, in all workspaces and in one.</summary>
    public async Task<(long All, long InWorkspace)> FullTextCandidatesAsync(string condition, long workspaceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            SELECT COUNT_BIG(*), ISNULL(SUM(CASE WHEN m.WorkspaceId = @workspace THEN 1 ELSE 0 END), 0)
            FROM CONTAINSTABLE(dbo.Messages, Content, @condition) AS ft
            JOIN dbo.Messages AS m ON m.Id = ft.[KEY]
            """, connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@condition", System.Data.SqlDbType.NVarChar, 4000) { Value = condition });
        command.Parameters.AddWithValue("@workspace", workspaceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt32(1));
    }

    /// <summary>Reserved megabytes per table (data and indexes, including LOB pages).</summary>
    public async Task<IReadOnlyList<(string Table, long Rows, double ReservedMb)>> TableSizesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            SELECT t.name,
                   SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END),
                   SUM(ps.reserved_page_count) * 8 / 1024.0
            FROM sys.dm_db_partition_stats AS ps
            JOIN sys.tables AS t ON t.object_id = ps.object_id
            WHERE t.name IN (N'Messages', N'MessageEmbeddings', N'Sessions', N'SessionPeers')
            GROUP BY t.name
            ORDER BY t.name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var sizes = new List<(string, long, double)>();
        while (await reader.ReadAsync(ct))
        {
            sizes.Add((reader.GetString(0), reader.GetInt64(1), (double)reader.GetDecimal(2)));
        }

        return sizes;
    }

    /// <summary>The server's version, its visible CPUs and memory, and its memory target.</summary>
    public async Task<string> ServerAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(64)),
                   cpu_count, physical_memory_kb / 1024, committed_target_kb / 1024,
                   CAST(SERVERPROPERTY('IsFullTextInstalled') AS int)
            FROM sys.dm_os_sys_info
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return $"SQL Server {reader.GetString(0)} ({(reader.IsDBNull(1) ? "RTM" : reader.GetString(1))}), " +
            $"{reader.GetInt32(2)} CPUs visible, {reader.GetInt64(3):N0} MB visible memory, {reader.GetInt64(4):N0} MB memory target, " +
            $"full-text installed: {reader.GetInt32(5) == 1}";
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>The plan's index accesses, as distinct <c>Operation Index</c> pairs, for example <c>Index Seek IX_Messages_Workspace_Peer</c>.</summary>
    private static string IndexOperations(string plan)
    {
        var operations = XDocument.Parse(plan).Descendants(Showplan + "RelOp")
            .Select(op => (Op: (string?)op.Attribute("PhysicalOp"), Index: op.Elements().Elements(Showplan + "Object")
                .Select(o => (string?)o.Attribute("Index") ?? (string?)o.Attribute("Table")).FirstOrDefault()))
            .Where(op => op.Index is not null)
            .Select(op => $"{op.Op} {op.Index!.Trim('[', ']')}")
            .Distinct();
        return string.Join("; ", operations);
    }
}

/// <summary>Per-execution averages of one cached statement, and the index operations of its plan.</summary>
internal sealed record StatementStats(
    long Executions, double ElapsedMs, double CpuMs, long LogicalReads, long PhysicalReads, long Dop, string IndexOperations);
