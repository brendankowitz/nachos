using System.Data;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>What the server says about the database a connection string points at.</summary>
/// <param name="Target">The dacpac for the server's platform.</param>
/// <param name="Status">Where the database stands against the embedded schema.</param>
/// <param name="DatabaseName">The server's own name for the database (<c>DB_NAME()</c>), whatever the connection string spelled.</param>
/// <param name="UntrustedConstraints">Enabled constraints whose existing rows were never validated.</param>
internal sealed record Observed(DacpacTarget Target, SchemaStatus Status, string DatabaseName, IReadOnlyList<string> UntrustedConstraints);

/// <summary>Reads the facts a schema decision is made from. Writes nothing.</summary>
internal static class SchemaProbe
{
    // master, tempdb, model and msdb are database ids 1 to 4 on SQL Server and Azure SQL Database.
    private const int LastSystemDatabaseId = 4;

    private const string DatabaseIdentitySql = "SELECT CAST(DB_ID() AS int), DB_NAME()";

    private const string ServerFactsSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS int),
               CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
               CASE WHEN EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0) THEN 1 ELSE 0 END,
               CASE WHEN OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NOT NULL
                     AND COL_LENGTH(N'dbo.SchemaVersion', N'Version') IS NOT NULL
                    THEN 1 ELSE 0 END
        """;

    private const string StampedVersionSql = "SELECT TOP (1) [Version] FROM [dbo].[SchemaVersion] WHERE [Id] = 1";

    private const string UntrustedConstraintsSql = """
        SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N'.' + QUOTENAME(name)
        FROM sys.check_constraints WHERE is_not_trusted = 1 AND is_disabled = 0 AND is_ms_shipped = 0
        UNION ALL
        SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N'.' + QUOTENAME(name)
        FROM sys.foreign_keys WHERE is_not_trusted = 1 AND is_disabled = 0 AND is_ms_shipped = 0
        """;

    /// <summary>
    /// Refuses a system database by the id the server reports. A connection string's spelling proves nothing: SQL Server ignores
    /// trailing spaces and case in a database name, so <c>"master "</c> and <c>MSDB</c> reach the system databases.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is one of the system databases.</exception>
    public static void RequireUserDatabase(int databaseId, string databaseName)
    {
        if (databaseId <= LastSystemDatabaseId)
        {
            throw new InvalidOperationException(
                $"The connection string reaches the system database '{databaseName}'. Nachos will not create or change its schema there; " +
                $"name a dedicated database in {SqlServerOptions.SectionName}:ConnectionString.");
        }
    }

    /// <exception cref="InvalidOperationException">The connection string reaches a system database.</exception>
    /// <exception cref="NotSupportedException">The server platform has no matching schema package.</exception>
    public static async Task<Observed> ReadAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // First, before anything else is read or decided: which database is this really?
        int databaseId;
        string databaseName;
        await using (var command = new SqlCommand(DatabaseIdentitySql, connection))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct))
        {
            await reader.ReadAsync(ct);
            databaseId = reader.GetInt32(0);
            databaseName = reader.GetString(1);
        }

        try
        {
            RequireUserDatabase(databaseId, databaseName);
        }
        catch (InvalidOperationException)
        {
            // A session sitting in model holds a lock that blocks every CREATE DATABASE; do not leave one in the pool.
            SqlConnection.ClearPool(connection);
            throw;
        }

        int engineEdition, majorVersion;
        bool hasObjects, hasStamp;
        await using (var command = new SqlCommand(ServerFactsSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct))
        {
            await reader.ReadAsync(ct);
            engineEdition = reader.GetInt32(0);
            majorVersion = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            hasObjects = reader.GetInt32(2) == 1;
            hasStamp = reader.GetInt32(3) == 1;
        }

        var target = DacpacCatalog.Select(engineEdition, majorVersion);

        int? deployed = null;
        if (hasStamp)
        {
            await using var command = new SqlCommand(StampedVersionSql, connection);
            var stamped = await command.ExecuteScalarAsync(ct);
            deployed = stamped is null or DBNull ? null : Convert.ToInt32(stamped, System.Globalization.CultureInfo.InvariantCulture);
        }

        var state = (hasObjects, deployed) switch
        {
            (false, _) => SchemaState.Empty,
            (true, null) => SchemaState.Unstamped,
            (true, var v) when v == SchemaInfo.CurrentVersion => SchemaState.Current,
            (true, var v) when v < SchemaInfo.CurrentVersion => SchemaState.Behind,
            _ => SchemaState.Ahead,
        };

        var untrusted = new List<string>();
        if (hasObjects)
        {
            await using var command = new SqlCommand(UntrustedConstraintsSql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                untrusted.Add(reader.GetString(0));
            }
        }

        return new Observed(target, new SchemaStatus(target.Platform, deployed, SchemaInfo.CurrentVersion, state), databaseName, untrusted);
    }
}