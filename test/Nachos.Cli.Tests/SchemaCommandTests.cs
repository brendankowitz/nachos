using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace Nachos.Cli.Tests;

[Collection(SqlServerDockerGroup.Name)]
public sealed class SchemaCommandTests(SqlServerFixture fixture)
{
    private static Task<CliRun> UpgradeAsync(string connectionString, params string[] flags) =>
        CliRun.RunAsync(["schema", "upgrade", "--connection", connectionString, .. flags]);

    private static async Task<JsonElement> StatusAsync(string connectionString)
    {
        var run = await CliRun.RunAsync("schema", "status", "--connection", connectionString);
        run.ExitCode.ShouldBe(0, run.Error);
        return JsonDocument.Parse(run.Out).RootElement.Clone();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ColumnCountAsync(string connectionString, string column)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Workspaces') AND name = N'{column}'", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> TableCountAsync(string connectionString, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM sys.tables WHERE name = N'{table}'", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> DeployedDatabaseAsync()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        (await UpgradeAsync(connectionString)).ExitCode.ShouldBe(0);
        return connectionString;
    }

    [Fact]
    public async Task Upgrade_EmptyDb_Exit0_ThenStatusCurrent()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        (await StatusAsync(connectionString)).GetProperty("state").GetString().ShouldBe("Empty");

        var run = await UpgradeAsync(connectionString);

        run.ExitCode.ShouldBe(0, run.Error);
        var status = await StatusAsync(connectionString);
        status.GetProperty("state").GetString().ShouldBe("Current");
        status.GetProperty("deployed").GetInt32().ShouldBe(status.GetProperty("current").GetInt32());
        status.GetProperty("platform").GetString().ShouldNotBeNullOrWhiteSpace();

        // Running it again is a no-op, not an error.
        (await UpgradeAsync(connectionString)).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ReportOnly_DoesNotApply()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        var run = await UpgradeAsync(connectionString, "--report-only");

        run.ExitCode.ShouldBe(0, run.Error);
        XDocument.Parse(run.Out).Root.ShouldNotBeNull();
        run.Error.ShouldContain("classification: AutoSafe");
        (await StatusAsync(connectionString)).GetProperty("state").GetString().ShouldBe("Empty");
        (await TableCountAsync(connectionString, "Workspaces")).ShouldBe(0);
    }

    [Fact]
    public async Task Report_WritesXmlToOut_AndPrintsClassification()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER DATABASE CURRENT SET PAGE_VERIFY NONE WITH ROLLBACK IMMEDIATE");
        SqlConnection.ClearAllPools();
        var path = Path.Combine(Path.GetTempPath(), $"nachos-report-{Guid.NewGuid():N}.xml");

        try
        {
            var run = await CliRun.RunAsync("schema", "report", "--connection", connectionString, "--out", path);

            // With --out the XML goes to the file and stdout stays empty; the summary is on stderr either way.
            run.ExitCode.ShouldBe(0, run.Error);
            run.Out.ShouldBeEmpty();
            run.Error.ShouldContain("classification: Unsafe");
            run.Error.ShouldContain("hasPendingChanges: True");
            run.Error.ShouldContain("PAGE_VERIFY");
            run.Error.ShouldContain(path);
            XDocument.Load(path).Root.ShouldNotBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("report")]
    [InlineData("upgrade --report-only")]
    public async Task Report_WithoutOut_PrintsTheFullXmlToStdout_AndTheSummaryToStderr(string entryPoint)
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL");
        string[] args = ["schema", .. entryPoint.Split(' '), "--connection", connectionString];

        var run = await CliRun.RunAsync(args);

        run.ExitCode.ShouldBe(0, run.Error);
        // stdout is the report and nothing else, so redirecting it gives a well-formed file.
        var report = XDocument.Parse(run.Out);
        var names = report.Descendants().Select(e => e.Name.LocalName).ToList();
        names.ShouldContain("Operation");
        report.Descendants().Where(e => e.Name.LocalName == "Alert").Select(e => (string?)e.Attribute("Name")).ShouldContain("DataIssue");
        run.Out.ShouldContain("NotInTheModel");
        run.Out.ShouldContain("data loss could occur");
        // The summary is for a person, on the other stream.
        run.Error.ShouldContain("classification: Unsafe");
        run.Error.ShouldContain("hasPendingChanges: True");
        run.Error.ShouldNotContain("<DeploymentReport");
        run.Out.ShouldNotContain("classification:");
        (await ColumnCountAsync(connectionString, "NotInTheModel")).ShouldBe(1);
    }

    [Fact]
    public async Task RedirectedStreams_AreUtf8WithoutBom_SoANonAsciiReportParses_RealProcess()
    {
        // Outside ASCII and outside every OEM code page at once: a console-code-page writer either mangles it or loses it.
        const string column = "Größe_Ж";
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, $"ALTER TABLE dbo.Workspaces ADD [{column}] int NULL");

        // RunProcessAsync decodes both streams as strict UTF-8 and keeps a byte order mark as U+FEFF.
        var report = await CliRun.RunProcessAsync("schema", "report", "--connection", connectionString);
        var refused = await CliRun.RunProcessAsync("schema", "upgrade", "--connection", connectionString);

        report.ExitCode.ShouldBe(0, report.Error);
        report.Out.ShouldNotStartWith("\uFEFF");
        report.Error.ShouldNotStartWith("\uFEFF");
        var document = XDocument.Parse(report.Out);
        document.Declaration?.Encoding.ShouldBe("utf-8", StringCompareShould.IgnoreCase);
        document.ToString().ShouldContain(column);
        report.Error.ShouldContain("classification: Unsafe");

        refused.ExitCode.ShouldBe(2);
        refused.Error.ShouldNotStartWith("\uFEFF");
        refused.Error.ShouldContain(column);
    }

    [Fact]
    public async Task AllowDataLoss_WithoutApproveReviewed_Exit1()
    {
        // Rejected before any connection is attempted, so the connection string never has to work.
        var run = await UpgradeAsync("Server=localhost,1;Database=nachos_unused", "--allow-data-loss");

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("--approve-reviewed");
    }

    [Fact]
    public async Task Upgrade_UnsafeDrift_Exit2_PrintsReasons()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER DATABASE CURRENT SET PAGE_VERIFY NONE WITH ROLLBACK IMMEDIATE");
        SqlConnection.ClearAllPools();

        var run = await UpgradeAsync(connectionString);

        run.ExitCode.ShouldBe(2);
        run.Error.ShouldContain("PAGE_VERIFY");
        run.Error.ShouldContain("--approve-reviewed");
        run.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upgrade_UnsafeDrift_ApproveReviewed_Exit0()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER DATABASE CURRENT SET PAGE_VERIFY NONE WITH ROLLBACK IMMEDIATE");
        SqlConnection.ClearAllPools();

        var run = await UpgradeAsync(connectionString, "--approve-reviewed");

        run.ExitCode.ShouldBe(0, run.Error);
        (await UpgradeAsync(connectionString, "--report-only")).Error.ShouldContain("classification: AutoSafe");
    }

    [Fact]
    public async Task Upgrade_Unstamped_Exit2_ThenAdopt_Exit0()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(connectionString, "CREATE TABLE dbo.SomeoneElses (Id int NOT NULL)");
        // Adopting never changes the options of a database that is not Nachos's, so the operator matches the one Nachos needs.
        await ExecuteAsync(connectionString, "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE");
        SqlConnection.ClearAllPools();

        var refused = await UpgradeAsync(connectionString);

        refused.ExitCode.ShouldBe(2);
        refused.Error.ShouldContain("--adopt-unstamped");
        (await StatusAsync(connectionString)).GetProperty("state").GetString().ShouldBe("Unstamped");

        var adopted = await UpgradeAsync(connectionString, "--adopt-unstamped");

        adopted.ExitCode.ShouldBe(0, adopted.Error);
        (await StatusAsync(connectionString)).GetProperty("state").GetString().ShouldBe("Current");
    }

    [Fact]
    public async Task Upgrade_Ahead_Exit2()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "UPDATE dbo.SchemaVersion SET [Version] = [Version] + 1");

        var run = await UpgradeAsync(connectionString, "--approve-reviewed");

        run.ExitCode.ShouldBe(2);
        run.Error.ShouldContain("newer");
    }

    [Fact]
    public async Task Upgrade_Reviewed_DataLossBlocked_Exit2_NothingDropped_ThenAllowed_Exit0()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "INSERT dbo.Workspaces (Name, LifecycleState, CreatedAt) VALUES (N'w', 0, SYSDATETIMEOFFSET())");
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL");

        var blocked = await UpgradeAsync(connectionString, "--approve-reviewed");

        blocked.ExitCode.ShouldBe(2);
        blocked.Out.ShouldBeEmpty();
        blocked.Error.ShouldContain("NotInTheModel");
        blocked.Error.ShouldContain("Nothing was changed.");
        blocked.Error.ShouldContain("--allow-data-loss");
        // Never suggests a flag that was already passed.
        blocked.Error.ShouldNotContain("--approve-reviewed");
        (await ColumnCountAsync(connectionString, "NotInTheModel")).ShouldBe(1);

        var allowed = await UpgradeAsync(connectionString, "--approve-reviewed", "--allow-data-loss");

        allowed.ExitCode.ShouldBe(0, allowed.Error);
        (await ColumnCountAsync(connectionString, "NotInTheModel")).ShouldBe(0);
    }

    [Fact]
    public async Task Upgrade_NotReviewed_WithPossibleDataLoss_AdvisesBothFlagsInOneStep()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL");

        var refused = await UpgradeAsync(connectionString);

        refused.ExitCode.ShouldBe(2);
        refused.Out.ShouldBeEmpty();
        refused.Error.ShouldContain("NotInTheModel");
        refused.Error.ShouldContain("--approve-reviewed --allow-data-loss");
        (await ColumnCountAsync(connectionString, "NotInTheModel")).ShouldBe(1);
    }

    [Fact]
    public async Task Upgrade_GenuineFailure_WithAllowDataLoss_Exit1()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL");

        // Another session holds an exclusive lock on the table the deploy must alter, so the deploy fails after DacFx has
        // already reported a possible data loss. That is a failure, however much the message talks about data loss.
        await using var holder = new SqlConnection(connectionString);
        await holder.OpenAsync();
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (var hold = new SqlCommand("SELECT TOP (1) 1 FROM dbo.Workspaces WITH (TABLOCKX)", holder, transaction))
        {
            await hold.ExecuteScalarAsync();
        }

        var run = await UpgradeAsync(connectionString, "--approve-reviewed", "--allow-data-loss");

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldNotContain("Refused");
        run.Error.ShouldNotContain("re-run with");
        await transaction.RollbackAsync();
        (await ColumnCountAsync(connectionString, "NotInTheModel")).ShouldBe(1);
    }
}
