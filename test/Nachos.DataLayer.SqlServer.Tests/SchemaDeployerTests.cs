using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>Runs the real deployer against SQL Server 2025 in Docker (see <see cref="SqlServerFixture"/>).</summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed partial class SchemaDeployerTests(SqlServerFixture fixture)
{
    private static SchemaDeployer Deployer(string connectionString) => new(new SqlServerOptions { ConnectionString = connectionString });

    private static SchemaGate Gate(string connectionString, bool automatic)
    {
        var options = new SqlServerOptions { ConnectionString = connectionString, AutomaticSchemaDeploymentEnabled = automatic };
        return new SchemaGate(new SchemaDeployer(options), options);
    }

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> DeployedDatabaseAsync()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await Deployer(connectionString).DeployAsync(allowDataLoss: false, default);
        return connectionString;
    }

    private static Task<int> ColumnCountAsync(string connectionString, string table, string column) =>
        ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'{table}') AND name = N'{column}'");

    [Fact]
    public async Task EmptyDatabase_DeploysAndStampsVersion()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var deployer = Deployer(connectionString);
        (await deployer.GetStatusAsync(default)).State.ShouldBe(SchemaState.Empty);

        await deployer.DeployAsync(allowDataLoss: false, default);

        var status = await deployer.GetStatusAsync(default);
        status.State.ShouldBe(SchemaState.Current);
        status.Deployed.ShouldBe(SchemaInfo.CurrentVersion);
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Messages'")).ShouldBe(1);
    }

    [Fact]
    public async Task Deploy_SetsReadCommittedSnapshotOn()
    {
        var connectionString = await DeployedDatabaseAsync();

        (await ScalarAsync<bool>(connectionString, "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task PlatformSelection_Box2025_UsesSql2025Dacpac()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        (await Deployer(connectionString).GetStatusAsync(default)).Platform.ShouldBe("Sql2025");
    }

    [Fact]
    public async Task CurrentDatabase_IsNoOp()
    {
        var connectionString = await DeployedDatabaseAsync();
        var stampedAt = await ScalarAsync<DateTimeOffset>(connectionString, "SELECT AppliedAt FROM dbo.SchemaVersion");

        await Gate(connectionString, automatic: true).EnsureAsync(default);

        // A deploy would re-run the post-deployment script and move AppliedAt.
        (await ScalarAsync<DateTimeOffset>(connectionString, "SELECT AppliedAt FROM dbo.SchemaVersion")).ShouldBe(stampedAt);
    }

    [Fact]
    public async Task GateEnabled_Empty_Bootstraps()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        await Gate(connectionString, automatic: true).EnsureAsync(default);

        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
    }

    [Fact]
    public async Task GateDisabled_Empty_ThrowsWithRemedy()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(connectionString, automatic: false).EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Empty);
    }

    [Fact]
    public async Task UnsafeDiff_Refused()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL");
        var deployer = Deployer(connectionString);

        var report = await deployer.DeployAsync(allowDataLoss: false, default);

        report.Classification.ShouldBe(DeployClassification.Unsafe);
        (await ColumnCountAsync(connectionString, "dbo.Workspaces", "NotInTheModel")).ShouldBe(1);
    }

    [Fact]
    public async Task UnsafeDiff_AppliedOnlyWhenDataLossIsAllowed()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL");

        await Deployer(connectionString).DeployAsync(allowDataLoss: true, default);

        (await ColumnCountAsync(connectionString, "dbo.Workspaces", "NotInTheModel")).ShouldBe(0);
    }

    [Fact]
    public async Task BehindDatabase_UnsafeDiff_RefusedByGate()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL; UPDATE dbo.SchemaVersion SET Version = 0");

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(connectionString, automatic: true).EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        (await ColumnCountAsync(connectionString, "dbo.Workspaces", "NotInTheModel")).ShouldBe(1);
    }

    [Fact]
    public async Task NonEmptyUnstampedDatabase_NeverAutoDeployed()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(connectionString, "CREATE TABLE dbo.SomeoneElses (Id int NOT NULL)");
        var gate = Gate(connectionString, automatic: true);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => gate.EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Unstamped);
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Workspaces'")).ShouldBe(0);
    }

    [Fact]
    public async Task AheadDatabase_NeverDowngraded()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, $"UPDATE dbo.SchemaVersion SET Version = {SchemaInfo.CurrentVersion + 1}");
        var gate = Gate(connectionString, automatic: true);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => gate.EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        var status = await Deployer(connectionString).GetStatusAsync(default);
        status.State.ShouldBe(SchemaState.Ahead);
        status.Deployed.ShouldBe(SchemaInfo.CurrentVersion + 1);
    }

    [Fact]
    public async Task AllIndexKeys_WithinSqlLimits()
    {
        var connectionString = await DeployedDatabaseAsync();
        const string sql = """
            SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, i.type AS IndexType,
                   SUM(CAST(c.max_length AS int)) AS KeyBytes
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id AND t.is_ms_shipped = 0
            WHERE i.type IN (1, 2)
            GROUP BY i.object_id, i.index_id, i.name, i.type
            """;

        var checkedIndexes = 0;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            checkedIndexes++;
            var clustered = reader.GetByte(2) == 1;
            var limit = clustered ? 900 : 1700;
            reader.GetInt32(3).ShouldBeLessThanOrEqualTo(limit, $"{reader.GetString(0)}.{reader.GetString(1)} key size");
        }

        checkedIndexes.ShouldBeGreaterThan(10);
    }

    [Fact]
    public void PostDeployVersion_MatchesSchemaInfo()
    {
        var script = File.ReadAllText(RepoPath("src", "DataLayer", "Nachos.DataLayer.SqlServer.Database", "Scripts", "Script.PostDeployment.sql"));
        var marked = script.Split('\n').Single(line => line.Contains("SCHEMA VERSION LITERAL", StringComparison.Ordinal));

        var literal = SelectVersionLiteral().Match(marked);

        literal.Success.ShouldBeTrue($"no 'AS [Version]' literal on the marked line: {marked}");
        int.Parse(literal.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(SchemaInfo.CurrentVersion);
    }

    [GeneratedRegex(@"\b(?<version>\d+)\s+AS\s+\[Version\]", RegexOptions.IgnoreCase)]
    private static partial Regex SelectVersionLiteral();

    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine([(directory ?? throw new InvalidOperationException("Nachos.slnx not found above the test output.")).FullName, .. parts]);
    }
}