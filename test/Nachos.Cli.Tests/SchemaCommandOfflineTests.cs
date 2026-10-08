using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>Schema command behaviour that needs no database, so it is covered without Docker.</summary>
public sealed class SchemaCommandOfflineTests
{
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
