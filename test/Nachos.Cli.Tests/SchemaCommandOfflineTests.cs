using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>Schema command behaviour that needs no database, so it is covered without Docker.</summary>
public sealed class SchemaCommandOfflineTests
{
    [Theory]
    [InlineData("--allow-data-loss", "true", "--allow-data-loss", "false")]
    [InlineData("--allow-data-loss", "--allow-data-loss")]
    [InlineData("--approve-reviewed", "false", "--approve-reviewed", "true", "--allow-data-loss")]
    public async Task Upgrade_RepeatedFlag_Exit1_WithAUsageError_NotACrash(params string[] flags)
    {
        var run = await CliRun.RunAsync(["schema", "upgrade", "--connection", "Server=127.0.0.1,1;Database=nachos_unused", .. flags]);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("--help");
        run.Error.ShouldNotContain("Unhandled");
        run.Error.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Upgrade_RepeatedFlagsWithBothSet_AreAccepted_AndReachTheConnection()
    {
        // Repeating a bare flag is harmless, and the rule "--allow-data-loss needs --approve-reviewed" still holds.
        var run = await CliRun.RunAsync(
            "schema", "upgrade", "--connection", "Server=127.0.0.1,1;Database=nachos_unused;Connect Timeout=3;Encrypt=false",
            "--approve-reviewed", "--approve-reviewed", "--allow-data-loss", "--allow-data-loss");

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldStartWith("error:");
        run.Error.ShouldNotContain("--help");
    }

    [Fact]
    public async Task Upgrade_ConnectionFailure_Exit1()
    {
        var run = await CliRun.RunAsync(
            "schema", "upgrade", "--connection", "Server=127.0.0.1,1;Database=nachos_unused;Connect Timeout=3;Encrypt=false");

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldNotBeNullOrWhiteSpace();
    }
}
