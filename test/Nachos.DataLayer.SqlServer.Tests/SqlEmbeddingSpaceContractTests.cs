using Microsoft.Data.SqlClient;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.Testing;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The embedding model stamp, run against SQL Server 2025 in Docker, each test on a database of its own.</summary>
/// <remarks>
/// The stamp and a reset are database-wide, so these tests cannot share the contract database. The base class's
/// <see cref="IAsyncLifetime"/> methods are not virtual, so the interface is re-implemented to create the database before
/// <see cref="CreateStore"/> (which is synchronous) runs.
/// </remarks>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlEmbeddingSpaceContractTests(SqlServerFixture fixture) : EmbeddingSpaceContractTests, IAsyncLifetime
{
    private SqlTestDatabase? _database;

    private SqlTestDatabase Database => _database ?? throw new InvalidOperationException("The database is not initialized.");

    async Task IAsyncLifetime.InitializeAsync()
    {
        _database = await SqlTestDatabase.CreateAsync(fixture);
        await InitializeAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync();

    protected override IMemoryStore CreateStore(TimeProvider clock) => Database.CreateStore(clock);

    [Fact]
    public async Task UnreachableServer_IsUnavailable_AMissingTableIsNot()
    {
        var unreachable = new SqlServerOptions { ConnectionString = "Server=tcp:127.0.0.1,1;Database=nachos;Connect Timeout=2;Encrypt=false" };
        var down = SqlTestDatabase.CreateStore(unreachable, new SchemaGate(new SchemaDeployer(unreachable), unreachable), TimeProvider.System);
        var store = Database.CreateStore(TimeProvider.System);
        (await store.Embeddings.GetSpaceAsync(default)).ShouldBeNull(); // the schema gate is verified before the table goes
        await SchemaTestSql.ExecuteAsync(Database.ConnectionString, "DROP TABLE dbo.EmbeddingSpace");

        var connectFailure = await Should.ThrowAsync<SqlException>(() => down.Embeddings.GetSpaceAsync(default));
        var missingTable = await Should.ThrowAsync<SqlException>(() => store.Embeddings.GetSpaceAsync(default));

        down.IsUnavailableFailure(connectFailure).ShouldBeTrue();
        store.IsUnavailableFailure(missingTable).ShouldBeFalse();
        store.IsUnavailableFailure(new InvalidOperationException("The schema gate refused.")).ShouldBeFalse();
    }
}
