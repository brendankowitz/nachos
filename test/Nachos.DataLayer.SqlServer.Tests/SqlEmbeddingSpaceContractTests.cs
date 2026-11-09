using Nachos.Abstractions.Stores;
using Nachos.Testing;

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
}
