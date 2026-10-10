using Microsoft.Data.SqlClient;
using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>
/// Errors that a real server returns name the user and the database as SqlClient parsed them from <c>--connection</c>, which can
/// differ from the text the operator typed (quoted values, doubled quotes, a ';' inside quotes). None of it may reach the output, and
/// a password stays redacted even when it is an ordinary word that the server's message also uses.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class ServerErrorDisclosureTests(SqlServerFixture fixture)
{
    private string Server => new SqlConnectionStringBuilder(fixture.ServerConnectionString).DataSource;

    private string AdminPassword => new SqlConnectionStringBuilder(fixture.ServerConnectionString).Password;

    private const string Options = "TrustServerCertificate=True;Connect Timeout=10";

    [Theory]
    [InlineData("User ID=sa;Password=login", "login")]
    [InlineData("User ID=\"Uv3Usr;Tq8Tail\";Password=Wr0ngPw7", "Uv3Usr|Tq8Tail|Wr0ngPw7")]
    [InlineData("User ID='Ab''Cd9Usr';Password=Wr0ngPw7", "Cd9Usr|Wr0ngPw7")]
    public async Task FailedLogin_SaysSo_WithoutTheUserOrPassword_RealProcess(string credentials, string secrets)
    {
        var cs = $"Server={Server};Initial Catalog=nachos_probe;{credentials};{Options}";

        var run = await CliRun.RunProcessAsync("schema", "status", "--connection", cs);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("failed for user");
        run.Error.ShouldNotContain("malformed");
        ConnectionStringDisclosureTests.ShouldNotLeak(run, secrets.Split('|'));
    }

    [Fact]
    public async Task MissingDatabase_SaysSo_WithoutItsName_RealProcess()
    {
        var cs = $"Server={Server};Initial Catalog='Rz8''Db9Qx';User ID=sa;Password={AdminPassword};{Options}";

        var run = await CliRun.RunProcessAsync("schema", "status", "--connection", cs);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("Cannot open database");
        ConnectionStringDisclosureTests.ShouldNotLeak(run, "Rz8", "Db9Qx", AdminPassword);
    }
}
