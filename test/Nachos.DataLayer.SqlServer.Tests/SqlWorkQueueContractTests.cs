using Nachos.Abstractions.Stores;
using Nachos.Testing;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The work-queue and transactional-enqueue contract, run against SQL Server 2025 in Docker.</summary>
/// <remarks>
/// The base class's <see cref="IAsyncLifetime"/> methods are not virtual, so the interface is re-implemented here to make
/// the shared database available before <see cref="CreateStore"/> (which is synchronous) runs.
/// </remarks>
[Collection(M2StoreContracts.Name)]
public sealed class SqlWorkQueueContractTests(SqlServerFixture fixture) : WorkQueueContractTests, IAsyncLifetime
{
    private SqlTestDatabase? _database;

    private SqlTestDatabase Database => _database ?? throw new InvalidOperationException("The database is not initialized.");

    async Task IAsyncLifetime.InitializeAsync()
    {
        _database = await SqlTestDatabase.GetAsync(fixture, "m2-queue-contract");
        await InitializeAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync();

    protected override IMemoryStore CreateStore(TimeProvider clock) => Database.CreateStore(clock);

    protected override Task ResetQueueAsync(IMemoryStore store) => Database.ResetQueueAsync();
}
