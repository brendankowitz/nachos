using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The M2 memory and queue tables (Collections, Conclusions, ConclusionSources, PeerCards, WorkItems, WorkUnitLeases) deploy
/// additively at schema version 1, and a database stamped current without them is refused with the fix command.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class M2SchemaTests(SqlServerFixture fixture)
{
    private static readonly string[] M2Tables = ["Collections", "ConclusionSources", "Conclusions", "PeerCards", "WorkItems", "WorkUnitLeases"];

    private static SchemaDeployer Deployer(string connectionString) => new(new SqlServerOptions { ConnectionString = connectionString });

    private static SchemaGate Gate(string connectionString)
    {
        var options = new SqlServerOptions { ConnectionString = connectionString, AutomaticSchemaDeploymentEnabled = true };
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

    private static Task<int> M2TableCountAsync(string connectionString) =>
        ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM sys.tables WHERE name IN ({string.Join(", ", M2Tables.Select(t => $"N'{t}'"))})");

    private async Task<string> DeployedDatabaseAsync()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        (await Deployer(connectionString).DeployAsync(DeployApproval.AutoSafeOnly, allowDataLoss: false, adoptUnstamped: false, default)).Applied.ShouldBeTrue();
        return connectionString;
    }

    // What a database deployed by an earlier M1 build looks like: stamped version 1, none of the M2 tables.
    private static Task DropM2TablesAsync(string connectionString) =>
        ExecuteAsync(
            connectionString,
            """
            DROP TABLE [dbo].[ConclusionSources];
            DROP TABLE [dbo].[Conclusions];
            DROP TABLE [dbo].[Collections];
            DROP TABLE [dbo].[PeerCards];
            DROP TABLE [dbo].[WorkItems];
            DROP TABLE [dbo].[WorkUnitLeases];
            """);

    [Fact]
    public async Task M2Tables_DeployToEmptyDatabase()
    {
        var connectionString = await DeployedDatabaseAsync();

        (await M2TableCountAsync(connectionString)).ShouldBe(M2Tables.Length);
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
        (await Deployer(connectionString).ReportAsync(default)).HasPendingChanges.ShouldBeFalse();
        SchemaInfo.CurrentVersion.ShouldBe(1);
    }

    [Fact]
    public async Task M1Database_ReviewedUpgrade_AddsM2Tables()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "INSERT [dbo].[Workspaces] ([Name], [LifecycleState], [CreatedAt]) VALUES (N'kept', 0, SYSDATETIMEOFFSET())");
        await DropM2TablesAsync(connectionString);
        var deployer = Deployer(connectionString);

        var report = await deployer.ReportAsync(default);
        report.HasPendingChanges.ShouldBeTrue();
        report.Classification.ShouldBe(DeployClassification.AutoSafe, string.Join(' ', report.Reasons));

        var applied = await deployer.DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, default);

        applied.Applied.ShouldBeTrue();
        (await M2TableCountAsync(connectionString)).ShouldBe(M2Tables.Length);
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM [dbo].[Workspaces] WHERE [Name] = N'kept'")).ShouldBe(1);
        (await deployer.ReportAsync(default)).HasPendingChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task StampedCurrent_MissingTable_FailsReadinessWithFixCommand()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "DROP TABLE [dbo].[WorkUnitLeases]");
        const string Expected =
            "Schema is stamped current but table WorkUnitLeases is missing; recreate the database or run 'nachos schema upgrade --approve-reviewed'.";

        var status = await Should.ThrowAsync<InvalidOperationException>(() => Deployer(connectionString).GetStatusAsync(default));
        var gate = Gate(connectionString);
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => gate.EnsureAsync(default));
        var refusedAgain = await Should.ThrowAsync<InvalidOperationException>(() => gate.EnsureAsync(default));

        status.Message.ShouldBe(Expected);
        refused.Message.ShouldBe(Expected);
        refusedAgain.Message.ShouldBe(Expected);

        await Deployer(connectionString).DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, default);

        await Gate(connectionString).EnsureAsync(default);
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

        var checkedTables = new HashSet<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (!M2Tables.Contains(table))
            {
                continue;
            }

            checkedTables.Add(table);
            var limit = reader.GetByte(2) == 1 ? 900 : 1700;
            reader.GetInt32(3).ShouldBeLessThanOrEqualTo(limit, $"{table}.{reader.GetString(1)} key size");
        }

        checkedTables.ShouldBe(M2Tables, ignoreOrder: true);
    }

    [Theory]
    [InlineData("Azure")]
    [InlineData("Sql2025")]
    public void VectorColumns_Dimension1536_OnBothDacpacs(string platform)
    {
        var target = platform == "Azure" ? DacpacCatalog.Azure : DacpacCatalog.Sql2025;

        using var archive = new ZipArchive(target.Open(), ZipArchiveMode.Read);
        using var model = archive.GetEntry("model.xml")!.Open();
        var column = XDocument.Load(model).Descendants()
            .Single(e => e.Attribute("Type")?.Value == "SqlSimpleColumn" && e.Attribute("Name")?.Value == "[dbo].[Conclusions].[Embedding]");
        var specifier = column.Descendants().Single(e => e.Attribute("Type")?.Value == "SqlVectorTypeSpecifier");

        specifier.Elements().Where(e => e.Name.LocalName == "Property").Single(p => p.Attribute("Name")?.Value == "Dimension").Attribute("Value")!.Value.ShouldBe("1536");
    }

    [Fact]
    public async Task VectorColumn_Dimension1536_OnTheDeployedDatabase()
    {
        var connectionString = await DeployedDatabaseAsync();

        var dimensions = await ScalarAsync<int>(
            connectionString,
            "SELECT vector_dimensions FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Conclusions') AND name = N'Embedding'");

        dimensions.ShouldBe(1536);
    }

    [Fact]
    public async Task WorkItemsTaskType_RejectsUnknownValue()
    {
        var connectionString = await DeployedDatabaseAsync();
        static string Insert(int taskType) =>
            $$"""
            INSERT [dbo].[WorkItems] ([TaskType], [WorkUnitKey], [Payload], [Status], [AvailableAt], [TokenCount], [CreatedAt])
            VALUES ({{taskType}}, N'k', N'{}', 0, SYSDATETIMEOFFSET(), 0, SYSDATETIMEOFFSET())
            """;

        foreach (var reserved in Enumerable.Range(1, 8))
        {
            await ExecuteAsync(connectionString, Insert(reserved));
        }

        foreach (var unknown in new[] { 0, 9, 255 })
        {
            var rejected = await Should.ThrowAsync<SqlException>(() => ExecuteAsync(connectionString, Insert(unknown)));
            rejected.Message.ShouldContain("CK_WorkItems_TaskType");
        }
    }
}
