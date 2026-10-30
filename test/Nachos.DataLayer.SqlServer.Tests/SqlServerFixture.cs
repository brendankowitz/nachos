using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The SQL Server 2025 container shared by every Docker-backed test in this assembly: the parallel
/// <see cref="SqlServerDockerGroup"/> and the non-parallel <see cref="SqlM2StoreContracts"/> each get a fixture instance,
/// and both use the one container. Every test class works in databases of its own.
/// </summary>
/// <remarks>
/// The container lives for the whole test run and is not disposed by a fixture, because xunit disposes each collection's
/// fixture when that collection ends, while the other may still need the container. Testcontainers' resource reaper
/// removes it when the test process exits.
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<MsSqlContainer>> Shared = new(StartAsync);

    private readonly Task<MsSqlContainer> _container = Shared.Value;

    public async Task InitializeAsync() => await _container;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Creates a uniquely named empty database and returns a connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var container = await _container;
        var name = $"nachos_{Guid.NewGuid():N}";

        await using (var connection = new SqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"CREATE DATABASE [{name}]", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
    }

    private static async Task<MsSqlContainer> StartAsync()
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();
        await container.StartAsync();
        return container;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerDockerGroup : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
