using Nachos.Abstractions.Stores;
using Nachos.Testing;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The shared store contract, run against SQL Server 2025 in Docker.</summary>
/// <remarks>
/// The base class's <see cref="IAsyncLifetime"/> methods are not virtual, so the interface is re-implemented here to
/// make the shared database available before <see cref="CreateStore"/> (which is synchronous) runs.
/// </remarks>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlStoreContractTests(SqlServerFixture fixture) : StoreContractTests, IAsyncLifetime
{
    private SqlTestDatabase? _database;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _database = await SqlTestDatabase.GetAsync(fixture, "store-contract");
        await InitializeAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync();

    protected override IMemoryStore CreateStore(TimeProvider clock) =>
        (_database ?? throw new InvalidOperationException("The database is not initialized.")).CreateStore(clock);
}
