using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>
/// Runtime errors must not carry anything the operator typed into <c>--connection</c>: SqlClient quotes unsupported keywords
/// (lowercased), and a password that contains an unescaped ';' is split into fragments that read as keywords.
/// </summary>
public sealed class ConnectionStringDisclosureTests
{
    private static void ShouldNotLeak(CliRun run, params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            run.Out.ShouldNotContain(fragment, Case.Insensitive);
            run.Error.ShouldNotContain(fragment, Case.Insensitive);
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("report")]
    [InlineData("upgrade")]
    public async Task UnsupportedKeyword_IsNotEchoed_RealProcess(string command)
    {
        const string cs = "Server=localhost;Initial Catalog=nachos;FoOBarKeyWordXyz=1";

        var run = await CliRun.RunProcessAsync("schema", command, "--connection", cs);

        run.ExitCode.ShouldBe(1);
        ShouldNotLeak(run, "FoOBarKeyWordXyz", "foobarkeywordxyz");
        run.Error.ShouldContain("malformed");
    }

    [Fact]
    public async Task PasswordWithUnescapedSemicolon_IsNotEchoed_RealProcess()
    {
        // The password is "Hunter2;SecretTail"; the part after the ';' parses as a keyword.
        const string cs = "Server=localhost;Initial Catalog=nachos;User ID=sa;Password=Hunter2;SecretTail=Qq9";

        var run = await CliRun.RunProcessAsync("schema", "status", "--connection", cs);

        run.ExitCode.ShouldBe(1);
        ShouldNotLeak(run, "Hunter2", "SecretTail", "Qq9");
    }

    [Fact]
    public async Task Malformed_InProcess_SaysSoWithoutDetails()
    {
        var run = await CliRun.RunAsync("schema", "status", "--connection", "Server=localhost;Initial Catalog=nachos;Nope=1");

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldBe($"error: The connection string is malformed (details redacted).{Environment.NewLine}");
    }

    [Fact]
    public async Task UnreachableWellFormedConnection_GivesAUsefulNetworkError_WithoutTheStringsContents_RealProcess()
    {
        const string cs = "Server=127.0.0.1,1;Initial Catalog=nachos_unique_db_name;User ID=probe_user_name;Password=Zx9Secret;Connect Timeout=3;Encrypt=false";

        var run = await CliRun.RunProcessAsync("schema", "status", "--connection", cs);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("error:");
        run.Error.ShouldContain("SQL Server", Case.Insensitive);
        run.Error.ShouldNotContain("malformed");
        ShouldNotLeak(run, "Zx9Secret", "nachos_unique_db_name", "probe_user_name");
    }

    [Fact]
    public async Task ConnectionStringTokens_AreRedactedFromAnyRuntimeMessage_AnyCase()
    {
        // The guard against system databases names the database it found, which came from --connection.
        var run = await CliRun.RunAsync("schema", "status", "--connection", "Server=127.0.0.1,1;Initial Catalog=MsDb;Connect Timeout=3");

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("system database");
        run.Error.ShouldContain("<redacted>");
        ShouldNotLeak(run, "msdb");
    }
}
