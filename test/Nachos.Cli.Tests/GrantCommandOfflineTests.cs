using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>Grant command parsing and failure paths that need no database, so they run without Docker.</summary>
public sealed class GrantCommandOfflineTests
{
    private const string Cs = "Server=127.0.0.1,1;Initial Catalog=nachos_unused;User ID=probe_user;Password=Gr4ntSecret;Connect Timeout=3;Encrypt=false";

    private static Task<CliRun> RunAsync(params string[] args) => CliRun.RunAsync(args);

    private static void ShouldBeUsageError(CliRun run, string mention)
    {
        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain(mention);
        run.Error.ShouldContain("--help");
    }

    [Theory]
    [InlineData("--object-id", "o", "--role", "Nachos.Admin")]
    [InlineData("--connection", Cs, "--role", "Nachos.Admin")]
    [InlineData("--connection", Cs, "--object-id", "o")]
    public async Task Add_RequiresConnectionObjectIdAndRole(params string[] args)
    {
        foreach (var verb in new[] { "add", "remove" })
        {
            var run = await RunAsync(["grants", verb, .. args]);

            ShouldBeUsageError(run, "required");
            run.Error.ShouldNotContain("Gr4ntSecret");
        }
    }

    [Fact]
    public async Task List_RequiresConnection()
    {
        ShouldBeUsageError(await RunAsync("grants", "list"), "--connection");
    }

    [Theory]
    [InlineData("Nachos.Reader")]
    [InlineData("nachos.admin")]
    [InlineData("Admin")]
    [InlineData("")]
    public async Task UnknownRole_Exit1_WithoutEchoingIt(string role)
    {
        var run = await RunAsync("grants", "add", "--connection", Cs, "--object-id", "o", "--role", role);

        ShouldBeUsageError(run, "--role must be one of: Nachos.Admin, Nachos.Workspace.");
        if (role.Length > 0)
        {
            run.Error.ShouldNotContain($"'{role}'");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyObjectId_Exit1(string objectId)
    {
        ShouldBeUsageError(await RunAsync("grants", "add", "--connection", Cs, "--object-id", objectId, "--role", "Nachos.Admin"), "--object-id must not be empty");
        ShouldBeUsageError(await RunAsync("grants", "list", "--connection", Cs, "--object-id", objectId), "--object-id must not be empty");
    }

    [Theory]
    [InlineData(" o")]
    [InlineData("o ")]
    [InlineData("\to")]
    [InlineData(" o ")]
    public async Task ObjectIdWithLeadingOrTrailingWhitespace_Exit1_WithAFixedMessage(string objectId)
    {
        var message = "--object-id must not start or end with whitespace.";

        ShouldBeUsageError(await RunAsync("grants", "add", "--connection", Cs, "--object-id", objectId, "--role", "Nachos.Admin"), message);
        ShouldBeUsageError(await RunAsync("grants", "remove", "--connection", Cs, "--object-id", objectId, "--role", "Nachos.Admin"), message);
        ShouldBeUsageError(await RunAsync("grants", "list", "--connection", Cs, "--object-id", objectId), message);
    }

    [Fact]
    public async Task ObjectIdWithInnerWhitespace_IsAccepted()
    {
        // Only the ends are checked: the id is otherwise free-form. It reaches the (unreachable) server.
        var run = await RunAsync("grants", "list", "--connection", Cs, "--object-id", "a b");

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldNotContain("--object-id");
    }

    [Fact]
    public async Task ObjectIdLongerThanTheColumn_Exit1_ButTheLimitItselfIsAccepted()
    {
        ShouldBeUsageError(
            await RunAsync("grants", "add", "--connection", Cs, "--object-id", new string('x', 65), "--role", "Nachos.Admin"),
            "--object-id must be at most 64 characters");

        // 64 passes validation and reaches the (unreachable) server.
        var atLimit = await RunAsync("grants", "add", "--connection", Cs, "--object-id", new string('x', 64), "--role", "Nachos.Admin");
        atLimit.ExitCode.ShouldBe(1);
        atLimit.Error.ShouldNotContain("--object-id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad name")]
    [InlineData("ws/1")]
    public async Task InvalidWorkspace_Exit1(string workspace)
    {
        ShouldBeUsageError(
            await RunAsync("grants", "add", "--connection", Cs, "--object-id", "o", "--role", "Nachos.Workspace", "--workspace", workspace),
            "--workspace must be");
        ShouldBeUsageError(
            await RunAsync("grants", "remove", "--connection", Cs, "--object-id", "o", "--role", "Nachos.Workspace", "--workspace", workspace),
            "--workspace must be");
    }

    [Fact]
    public async Task AdminRole_WithAWorkspace_Exit1()
    {
        ShouldBeUsageError(
            await RunAsync("grants", "add", "--connection", Cs, "--object-id", "o", "--role", "Nachos.Admin", "--workspace", "ws1"),
            "--workspace applies only to the Nachos.Workspace role");
    }

    [Theory]
    [InlineData("--role", "add", "--object-id", "o", "--role", "Nachos.Admin", "--role", "X")]
    [InlineData("--object-id", "add", "--object-id", "o", "--object-id", "p", "--role", "Nachos.Admin")]
    [InlineData("--workspace", "add", "--object-id", "o", "--role", "Nachos.Workspace", "--workspace", "a", "--workspace", "b")]
    [InlineData("--role", "remove", "--object-id", "o", "--role", "Nachos.Admin", "--role", "Nachos.Admin")]
    [InlineData("--object-id", "remove", "--object-id", "o", "--object-id", "o", "--role", "Nachos.Admin")]
    [InlineData("--object-id", "list", "--object-id", "a", "--object-id", "b")]
    public async Task RepeatedOption_Exit1_WithAUsageError_NotACrash(string option, params string[] args)
    {
        var run = await RunAsync(["grants", .. args, "--connection", Cs]);

        // The parser's own error names the option; whether it says "expects a single argument" or the redacted form depends on
        // whether a short value such as "a" occurs in that sentence, so only the option is checked.
        ShouldBeUsageError(run, option);
        run.Error.ShouldNotContain("Unhandled");
        run.Error.ShouldNotContain("   at ");
        run.Error.ShouldNotContain("Gr4ntSecret");
    }

    [Fact]
    public async Task UnknownSubcommandOrOption_Exit1()
    {
        (await RunAsync("grants", "frobnicate")).ExitCode.ShouldBe(1);
        (await RunAsync("grants", "list", "--connection", Cs, "--workspace", "ws1")).ExitCode.ShouldBe(1);
    }

    [Theory]
    [InlineData("add", "--object-id", "o", "--role", "Nachos.Workspace", "--workspace", "ws1")]
    [InlineData("remove", "--object-id", "o", "--role", "Nachos.Admin")]
    [InlineData("list")]
    public async Task UnreachableServer_Exit1_WithoutTheConnectionStringsContents(params string[] args)
    {
        var run = await RunAsync(["grants", .. args, "--connection", Cs]);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("error:");
        ConnectionStringDisclosureTests.ShouldNotLeak(run, "Gr4ntSecret", "probe_user", "nachos_unused");
    }

    [Fact]
    public async Task MalformedConnectionString_Exit1_WithAFixedMessage()
    {
        var run = await RunAsync("grants", "list", "--connection", "Server=localhost;Initial Catalog=nachos;FoOBarKeyWordXyz=1");

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("malformed");
        ConnectionStringDisclosureTests.ShouldNotLeak(run, "foobarkeywordxyz");
    }

    [Fact]
    public async Task ActiveDirectoryDefaultAuthentication_IsAccepted_AndPassedToSqlClientUntouched()
    {
        // The azd hook uses this mode. It must get past parsing and reach the connection attempt, which fails here only
        // because nothing listens at the address.
        var run = await RunAsync(
            "grants", "list", "--connection", "Server=127.0.0.1,1;Initial Catalog=nachos_unused;Authentication=Active Directory Default;Connect Timeout=3");

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldNotContain("malformed");
        run.Error.ShouldContain("error:");
    }
}
