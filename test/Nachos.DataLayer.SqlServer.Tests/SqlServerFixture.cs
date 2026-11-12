using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The SQL Server 2025 container with Full-Text Search (built from <c>eng/docker/mssql-fts</c>) shared by every Docker-backed
/// test in this assembly: the parallel <see cref="SqlServerDockerGroup"/> and the non-parallel <see cref="SqlM2StoreContracts"/>
/// each get a fixture instance, and both use the one container. Every test class works in databases of its own.
/// </summary>
/// <remarks>
/// The container lives for the whole test run and is not disposed by a fixture, because xunit disposes each collection's
/// fixture when that collection ends, while the other may still need the container. Testcontainers' resource reaper
/// removes it when the test process exits. The image is built once per run; Docker's layer cache makes later runs cheap.
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<MsSqlContainer>> Shared = new(StartAsync);

    private readonly Task<MsSqlContainer> _container = Shared.Value;

    public async Task InitializeAsync() => await _container;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Creates a uniquely named empty database and returns a connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync() => await SqlTestContainers.CreateDatabaseAsync(await _container);

    private static async Task<MsSqlContainer> StartAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(SqlTestContainers.RepositoryPath("eng", "docker", "mssql-fts"))
            .WithDockerfile("Dockerfile")
            .WithName("nachos-test-mssql-fts:local")
            .WithDeleteIfExists(false)
            .Build();
        await image.CreateAsync();

        var container = new MsSqlBuilder(image).Build();
        await container.StartAsync();
        return container;
    }
}

/// <summary>
/// The stock SQL Server 2025 container, which has no Full-Text Search, for the tests that prove the database works where it is absent.
/// Same lifetime rules as <see cref="SqlServerFixture"/>.
/// </summary>
public sealed class SqlServerNoFtsFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<MsSqlContainer>> Shared = new(StartAsync);

    private readonly Task<MsSqlContainer> _container = Shared.Value;

    public async Task InitializeAsync() => await _container;

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task<string> CreateDatabaseAsync() => await SqlTestContainers.CreateDatabaseAsync(await _container);

    private static async Task<MsSqlContainer> StartAsync()
    {
        // The same update as the Full-Text Search image's base (MSSQL_UPDATE in eng/docker/mssql-fts/Dockerfile); bump both together.
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04").Build();
        await container.StartAsync();
        return container;
    }
}

internal static class SqlTestContainers
{
    public static async Task<string> CreateDatabaseAsync(MsSqlContainer container)
    {
        var name = $"nachos_{Guid.NewGuid():N}";

        await using (var connection = new SqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"CREATE DATABASE [{name}]", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>The directory at <paramref name="segments"/> under the repository root, found by walking up from the test binaries.</summary>
    public static string RepositoryPath(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"No '{Path.Combine(segments)}' directory above {AppContext.BaseDirectory}.");
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerDockerGroup : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

[CollectionDefinition(Name)]
public sealed class SqlServerNoFtsDockerGroup : ICollectionFixture<SqlServerNoFtsFixture>
{
    public const string Name = "SqlServerNoFts";
}
