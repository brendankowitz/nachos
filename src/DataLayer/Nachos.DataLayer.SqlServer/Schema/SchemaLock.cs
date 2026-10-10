using System.Data;
using Microsoft.Data.SqlClient;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// An exclusive lock (<c>sp_getapplock</c>, session-owned) that serializes schema changes to one database across
/// processes. It lives as long as its connection, so a crashed process cannot leave it held.
/// </summary>
/// <remarks>
/// On SQL Server the lock is taken in <c>master</c>, not in the database being changed: changing a database option such as
/// READ_COMMITTED_SNAPSHOT disconnects every session in that database, which would kill the holder and every waiter.
/// Azure SQL Database cannot reach another database, so there the lock lives in the target (and its option is already on).
/// Connecting to <c>master</c> works for any login through the <c>guest</c> user, which is enabled by default; a contained-database user, or a server where <c>guest</c> is disabled in <c>master</c>, is refused with an <see cref="InvalidOperationException"/> and nothing is changed.
/// </remarks>
internal sealed class SchemaLock : IAsyncDisposable
{
    private const string ResourcePrefix = "nachos-schema:";

    private readonly SqlConnection _connection;
    private readonly string _resource;

    private SchemaLock(SqlConnection connection, string resource)
    {
        _connection = connection;
        _resource = resource;
    }

    /// <param name="connectionString">The connection string of the database being changed.</param>
    /// <param name="database">The database being changed, as the server names it (<c>DB_NAME()</c>), not as the connection string spells it. Part of the lock's name, so databases do not block each other and <c>Nachos</c> and <c>nachos</c> are one lock.</param>
    /// <param name="inMaster">True to hold the lock in <c>master</c> (SQL Server), false to hold it in the target database (Azure SQL Database).</param>
    /// <param name="timeout">How long to wait for another holder.</param>
    /// <exception cref="TimeoutException">Another process held the lock for the whole <paramref name="timeout"/>.</exception>
    public static async Task<SchemaLock> AcquireAsync(string connectionString, string database, bool inMaster, TimeSpan timeout, CancellationToken ct)
    {
        // Unpooled: a pooled connection would go back to the pool still holding the lock if the release failed.
        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
        if (inMaster)
        {
            builder.InitialCatalog = "master";
        }

        var resource = ResourcePrefix + database;
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            try
            {
                await connection.OpenAsync(ct);
            }
            catch (SqlException failure) when (inMaster)
            {
                throw new InvalidOperationException(
                    "Could not connect to master to take the schema lock (" + failure.Message + ") " +
                    "If this is a permissions issue: contained-database users, and servers with guest disabled in master, cannot connect there; " +
                    "run the upgrade with a login that can. Nothing has been changed.",
                    failure);
            }

            await using var command = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = (int)timeout.TotalSeconds + 30,
            };
            command.Parameters.AddWithValue("@Resource", resource);
            command.Parameters.AddWithValue("@LockMode", "Exclusive");
            command.Parameters.AddWithValue("@LockOwner", "Session");
            command.Parameters.AddWithValue("@LockTimeout", (int)timeout.TotalMilliseconds);
            var result = new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
            command.Parameters.Add(result);
            await command.ExecuteNonQueryAsync(ct);

            return (int)result.Value switch
            {
                >= 0 => new SchemaLock(connection, resource),
                -1 => throw new TimeoutException(
                    $"Another Nachos process is changing the schema of '{database}': its lock was not available within {timeout.TotalSeconds:0} s. Retry when it has finished."),
                var code => throw new InvalidOperationException($"sp_getapplock('{resource}') failed with return code {code}."),
            };
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var release = new SqlCommand("sp_releaseapplock", _connection) { CommandType = CommandType.StoredProcedure };
            release.Parameters.AddWithValue("@Resource", _resource);
            release.Parameters.AddWithValue("@LockOwner", "Session");
            await release.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // A broken connection has already dropped its session locks; there is nothing left to release.
        }
        catch (InvalidOperationException)
        {
            // The connection was closed under us, which also released the lock.
        }

        await _connection.DisposeAsync();
    }
}