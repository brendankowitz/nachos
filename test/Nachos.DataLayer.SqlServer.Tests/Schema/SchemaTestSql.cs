using Microsoft.Data.SqlClient;
using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

internal static class SchemaTestSql
{
    public static readonly string[] M3Tables = ["MessageEmbeddings", "SessionSummaries", "EmbeddingSpace"];

    public static SchemaDeployer Deployer(string connectionString) => new(new SqlServerOptions { ConnectionString = connectionString });

    public static SchemaGate Gate(string connectionString)
    {
        var options = new SqlServerOptions { ConnectionString = connectionString, AutomaticSchemaDeploymentEnabled = true };
        return new SchemaGate(new SchemaDeployer(options), options);
    }

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DeployAsync(string connectionString) =>
        (await Deployer(connectionString).DeployAsync(DeployApproval.AutoSafeOnly, allowDataLoss: false, adoptUnstamped: false, default)).Applied.ShouldBeTrue();

    public static Task<int> M3TableCountAsync(string connectionString) =>
        ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM sys.tables WHERE name IN ({string.Join(", ", M3Tables.Select(t => $"N'{t}'"))})");
}
