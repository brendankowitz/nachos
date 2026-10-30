using Nachos.Abstractions.Stores;
using Nachos.Testing;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The conclusion, peer-card and embedding contract, run against SQL Server 2025 in Docker.</summary>
/// <remarks>
/// The base class's <see cref="IAsyncLifetime"/> methods are not virtual, so the interface is re-implemented here to make
/// the shared database available before <see cref="CreateStore"/> (which is synchronous) runs.
/// </remarks>
[Collection(M2StoreContracts.Name)]
public sealed class SqlMemoryContractTests(SqlServerFixture fixture) : MemoryContractTests, IAsyncLifetime
{
    private SqlTestDatabase? _database;

    private SqlTestDatabase Database => _database ?? throw new InvalidOperationException("The database is not initialized.");

    async Task IAsyncLifetime.InitializeAsync()
    {
        _database = await SqlTestDatabase.GetAsync(fixture, "m2-memory-contract");
        await InitializeAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync();

    protected override IMemoryStore CreateStore(TimeProvider clock) => Database.CreateStore(clock);

    /// <summary>
    /// Empties the queue and also purges every tombstone: the tests share one database and one start time, and
    /// <c>PurgeTombstonesAsync</c> spans workspaces, so earlier tests' tombstones would be counted too.
    /// </summary>
    protected override async Task ResetQueueAsync(IMemoryStore store)
    {
        await Database.ResetQueueAsync();
        await store.Conclusions.PurgeTombstonesAsync(TimeSpan.MinValue, CancellationToken.None);
    }
}
