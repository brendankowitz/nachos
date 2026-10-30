using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.DataLayer.SqlServer.Schema;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// A database with the Nachos schema deployed, shared by every test of one purpose (xunit creates a class instance per
/// test, so the database cannot live on the instance). The schema is deployed through <see cref="SchemaGate"/>, exactly
/// as a store with automatic deployment enabled would on first use.
/// </summary>
internal sealed class SqlTestDatabase
{
    private static readonly ConditionalWeakTable<SqlServerFixture, ConcurrentDictionary<string, Lazy<Task<SqlTestDatabase>>>> Databases = [];

    private SqlTestDatabase(SqlServerOptions options, SchemaGate gate)
    {
        Options = options;
        Gate = gate;
    }

    public SqlServerOptions Options { get; }

    public SchemaGate Gate { get; }

    public string ConnectionString => Options.ConnectionString;

    /// <summary>Returns the database for <paramref name="purpose"/>, creating and deploying it on first use.</summary>
    public static Task<SqlTestDatabase> GetAsync(SqlServerFixture fixture, string purpose) =>
        Databases.GetValue(fixture, _ => new())
            .GetOrAdd(purpose, _ => new Lazy<Task<SqlTestDatabase>>(() => CreateAsync(fixture)))
            .Value;

    /// <summary>A store over this database; every store shares the one verified gate.</summary>
    public SqlMemoryStore CreateStore(TimeProvider clock) => new(Options, Gate, clock, NullLogger<SqlMemoryStore>.Instance);

    /// <summary>
    /// Empties the work queue (items and leases). The queue spans workspaces, so the M2 contract suites, which claim
    /// whatever is due, start every test from an empty one.
    /// </summary>
    public async Task ResetQueueAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE dbo.WorkItems; DELETE dbo.WorkUnitLeases;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqlTestDatabase> CreateAsync(SqlServerFixture fixture)
    {
        var options = new SqlServerOptions
        {
            ConnectionString = await fixture.CreateDatabaseAsync(),
            AutomaticSchemaDeploymentEnabled = true,
        };
        var gate = new SchemaGate(new SchemaDeployer(options), options);
        await gate.EnsureAsync(CancellationToken.None);
        return new SqlTestDatabase(options, gate);
    }
}
