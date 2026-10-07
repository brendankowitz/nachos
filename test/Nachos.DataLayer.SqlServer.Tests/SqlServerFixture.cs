using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>One SQL Server 2025 container shared by every Docker-backed test in this assembly.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates a uniquely named empty database and returns a connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"nachos_{Guid.NewGuid():N}";

        await using (var connection = new SqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"CREATE DATABASE [{name}]", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerDockerGroup : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}