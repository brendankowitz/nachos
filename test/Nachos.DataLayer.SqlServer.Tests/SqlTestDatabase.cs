using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.DataLayer.SqlServer.Search;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// A database with the Nachos schema deployed, shared by every test of one purpose (xunit creates a class instance per
/// test, so the database cannot live on the instance). The schema is deployed through <see cref="SchemaGate"/>, exactly
/// as a store with automatic deployment enabled would on first use.
/// </summary>
internal sealed class SqlTestDatabase
{
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<string, Lazy<Task<SqlTestDatabase>>>> Databases = [];

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
        GetAsync(fixture, purpose, fixture.CreateDatabaseAsync);

    /// <summary>Returns the database for <paramref name="purpose"/> on the container without Full-Text Search.</summary>
    public static Task<SqlTestDatabase> GetAsync(SqlServerNoFtsFixture fixture, string purpose) =>
        GetAsync(fixture, purpose, fixture.CreateDatabaseAsync);

    /// <summary>Creates a database of its own, not shared with any other test, and deploys the schema.</summary>
    public static Task<SqlTestDatabase> CreateAsync(SqlServerFixture fixture) => CreateAsync(fixture.CreateDatabaseAsync);

    /// <inheritdoc cref="CreateAsync(SqlServerFixture)"/>
    public static Task<SqlTestDatabase> CreateAsync(SqlServerNoFtsFixture fixture) => CreateAsync(fixture.CreateDatabaseAsync);

    /// <summary>
    /// A store over this database; every store shares the one verified gate. Unless <paramref name="fullTextIndex"/> is
    /// given, its lexical search uses <c>LIKE</c>: the index maintenance it gets is never started, so never ready.
    /// </summary>
    public SqlMemoryStore CreateStore(TimeProvider clock, FullTextIndexMaintenance? fullTextIndex = null) =>
        new(Options, Gate, fullTextIndex ?? CreateFullTextIndexMaintenance(clock), clock, NullLogger<SqlMemoryStore>.Instance);

    /// <summary>A store over any database and gate, whose lexical search uses <c>LIKE</c> (see <see cref="CreateStore(TimeProvider, FullTextIndexMaintenance?)"/>).</summary>
    public static SqlMemoryStore CreateStore(SqlServerOptions options, SchemaGate gate, TimeProvider clock) =>
        new(options, gate, new FullTextIndexMaintenance(options, gate, clock, NullLogger<FullTextIndexMaintenance>.Instance), clock, NullLogger<SqlMemoryStore>.Instance);

    /// <summary>A full-text index maintenance over this database, not started.</summary>
    public FullTextIndexMaintenance CreateFullTextIndexMaintenance(TimeProvider clock) =>
        new(Options, Gate, clock, NullLogger<FullTextIndexMaintenance>.Instance);

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

    private static Task<SqlTestDatabase> GetAsync(object fixture, string purpose, Func<Task<string>> createDatabase) =>
        Databases.GetValue(fixture, _ => new())
            .GetOrAdd(purpose, _ => new Lazy<Task<SqlTestDatabase>>(() => CreateAsync(createDatabase)))
            .Value;

    private static async Task<SqlTestDatabase> CreateAsync(Func<Task<string>> createDatabase)
    {
        var options = new SqlServerOptions
        {
            ConnectionString = await createDatabase(),
            AutomaticSchemaDeploymentEnabled = true,
        };
        var gate = new SchemaGate(new SchemaDeployer(options), options);
        await gate.EnsureAsync(CancellationToken.None);
        return new SqlTestDatabase(options, gate);
    }
}
