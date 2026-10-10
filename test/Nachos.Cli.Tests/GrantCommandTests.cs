using System.Text.Json;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace Nachos.Cli.Tests;

[Collection(SqlServerDockerGroup.Name)]
public sealed class GrantCommandTests(SqlServerFixture fixture)
{
    private const string Admin = "Nachos.Admin";
    private const string Workspace = "Nachos.Workspace";

    private string Password => new SqlConnectionStringBuilder(fixture.ServerConnectionString).Password;

    /// <summary>Runs a grants command and checks that the connection string's password is in neither stream, whatever the outcome.</summary>
    private async Task<CliRun> GrantsAsync(string connectionString, params string[] args)
    {
        var run = await CliRun.RunAsync(["grants", .. args, "--connection", connectionString]);
        ConnectionStringDisclosureTests.ShouldNotLeak(run, Password);
        return run;
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
        var upgrade = await CliRun.RunAsync("schema", "upgrade", "--connection", connectionString);
        upgrade.ExitCode.ShouldBe(0, upgrade.Error);
        return connectionString;
    }

    private static Task AddWorkspaceAsync(string connectionString, string name) =>
        ExecuteAsync(connectionString, $"INSERT dbo.Workspaces (Name, LifecycleState, CreatedAt) VALUES (N'{name}', 0, SYSDATETIMEOFFSET())");

    private static string Row(string objectId, string role, string? workspace) =>
        $$"""{"objectId":"{{objectId}}","role":"{{role}}","workspace":{{(workspace is null ? "null" : $"\"{workspace}\"")}}}""";

    private static string Rows(params string[] rows) => $"[{string.Join(",", rows)}]";

    [Fact]
    public async Task AddListRemove()
    {
        var cs = await DeployedDatabaseAsync();
        await AddWorkspaceAsync(cs, "ws1");
        await AddWorkspaceAsync(cs, "ws2");

        // Add grants. The all-workspaces workspace grant of oid-b goes in before its admin grant, and both have no workspace, so only
        // the role decides their order. Output is one JSON line.
        foreach (var args in new[]
                 {
                     new[] { "--object-id", "oid-b", "--role", Workspace, "--workspace", "ws2" },
                     ["--object-id", "oid-b", "--role", Workspace],
                     ["--object-id", "oid-b", "--role", Admin],
                     ["--object-id", "oid-a", "--role", Workspace],
                     ["--object-id", "oid-b", "--role", Workspace, "--workspace", "ws1"],
                 })
        {
            var added = await GrantsAsync(cs, ["add", .. args]);
            added.ExitCode.ShouldBe(0, added.Error);
            added.Out.ShouldBe($"{{\"added\":true}}{Environment.NewLine}");
            added.Error.ShouldBeEmpty();
        }

        // Adding again is an idempotent no-op.
        var again = await GrantsAsync(cs, "add", "--object-id", "oid-b", "--role", Workspace, "--workspace", "ws1");
        again.ExitCode.ShouldBe(0, again.Error);

        // Sorted by object id, then role, then workspace (ordinal; no workspace first), not by insertion. Stable property order.
        var all = await GrantsAsync(cs, "list");
        all.ExitCode.ShouldBe(0, all.Error);
        all.Out.ShouldBe(Rows(
            Row("oid-a", Workspace, null),
            Row("oid-b", Admin, null),
            Row("oid-b", Workspace, null),
            Row("oid-b", Workspace, "ws1"),
            Row("oid-b", Workspace, "ws2")) + Environment.NewLine);

        var one = await GrantsAsync(cs, "list", "--object-id", "oid-b");
        one.Out.ShouldBe(Rows(
            Row("oid-b", Admin, null),
            Row("oid-b", Workspace, null),
            Row("oid-b", Workspace, "ws1"),
            Row("oid-b", Workspace, "ws2")) + Environment.NewLine);

        // The output is valid JSON with the documented shape, with an unknown object listing as an empty array.
        JsonDocument.Parse(one.Out).RootElement.GetArrayLength().ShouldBe(4);
        (await GrantsAsync(cs, "list", "--object-id", "nobody")).Out.ShouldBe($"[]{Environment.NewLine}");

        // Remove one grant, then again (a no-op), and one that never existed, including for a workspace that does not exist.
        foreach (var args in new[]
                 {
                     new[] { "--object-id", "oid-b", "--role", Workspace, "--workspace", "ws1" },
                     ["--object-id", "oid-b", "--role", Workspace, "--workspace", "ws1"],
                     ["--object-id", "oid-zzz", "--role", Admin],
                     ["--object-id", "oid-b", "--role", Workspace, "--workspace", "missing"],
                 })
        {
            var removed = await GrantsAsync(cs, ["remove", .. args]);
            removed.ExitCode.ShouldBe(0, removed.Error);
            removed.Out.ShouldBe($"{{\"removed\":true}}{Environment.NewLine}");
        }

        (await GrantsAsync(cs, "list")).Out.ShouldBe(Rows(
            Row("oid-a", Workspace, null),
            Row("oid-b", Admin, null),
            Row("oid-b", Workspace, null),
            Row("oid-b", Workspace, "ws2")) + Environment.NewLine);
    }
    [Fact]
    public async Task AddToAMissingWorkspace_Exit1_NotFound()
    {
        var cs = await DeployedDatabaseAsync();

        var run = await GrantsAsync(cs, "add", "--object-id", "oid-a", "--role", Workspace, "--workspace", "nope");

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("Workspace 'nope' not found.");
        (await GrantsAsync(cs, "list")).Out.ShouldBe($"[]{Environment.NewLine}");
    }

    [Theory]
    [InlineData("add", "--object-id", "oid-a", "--role", Workspace)]
    [InlineData("remove", "--object-id", "oid-a", "--role", Workspace)]
    [InlineData("list")]
    public async Task SchemaNotDeployed_Exit2_AndNothingIsCreated(params string[] args)
    {
        var cs = await fixture.CreateDatabaseAsync();

        var run = await GrantsAsync(cs, args);

        run.ExitCode.ShouldBe(2);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("Refused:");
        run.Error.ShouldContain("nachos schema upgrade");
        var status = await CliRun.RunAsync("schema", "status", "--connection", cs);
        JsonDocument.Parse(status.Out).RootElement.GetProperty("state").GetString().ShouldBe("Empty");
    }

    [Fact]
    public async Task UnstampedDatabase_Exit2_PointsAtAdopt()
    {
        var cs = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(cs, "CREATE TABLE dbo.SomeoneElses (Id int NOT NULL)");

        var run = await GrantsAsync(cs, "list");

        run.ExitCode.ShouldBe(2);
        run.Error.ShouldContain("--adopt-unstamped");
    }

    [Fact]
    public async Task BehindAndAheadDatabases_Exit2()
    {
        var cs = await DeployedDatabaseAsync();

        await ExecuteAsync(cs, "UPDATE dbo.SchemaVersion SET [Version] = 0");
        var behind = await GrantsAsync(cs, "list");
        behind.ExitCode.ShouldBe(2);
        behind.Error.ShouldContain("behind");

        await ExecuteAsync(cs, "UPDATE dbo.SchemaVersion SET [Version] = [Version] + 100");
        var ahead = await GrantsAsync(cs, "add", "--object-id", "oid-a", "--role", Admin);
        ahead.ExitCode.ShouldBe(2);
        ahead.Error.ShouldContain("newer");
    }

    [Fact]
    public async Task FailedLogin_Exit1_WithoutThePassword_RealProcess()
    {
        var builder = new SqlConnectionStringBuilder(fixture.ServerConnectionString)
        {
            InitialCatalog = "master",
            Password = "Wr0ngPw7-for-grants",
            TrustServerCertificate = true,
            ConnectTimeout = 10,
        };

        var run = await CliRun.RunProcessAsync("grants", "list", "--connection", builder.ConnectionString);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        ConnectionStringDisclosureTests.ShouldNotLeak(run, "Wr0ngPw7-for-grants", Password);
    }
}
