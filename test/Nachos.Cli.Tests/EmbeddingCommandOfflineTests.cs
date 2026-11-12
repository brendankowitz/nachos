using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary><c>nachos embeddings reset</c> usage errors, which are reported before any connection is opened.</summary>
public sealed class EmbeddingCommandOfflineTests
{
    private const string Unreachable = "Server=tcp:127.0.0.1,1;Database=nachos;Connect Timeout=1";

    [Theory]
    [InlineData(" bge-small-en-v1.5")]
    [InlineData("")]
    public async Task InvalidModel_IsAUsageError(string model)
    {
        var run = await CliRun.RunAsync("embeddings", "reset", "--model", model, "--connection", Unreachable);

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("--model must be 1-128 characters, without leading or trailing whitespace.");
    }

    [Fact]
    public async Task BatchSizeBelowOne_IsAUsageError()
    {
        var run = await CliRun.RunAsync("embeddings", "reset", "--model", "m", "--batch-size", "0", "--connection", Unreachable);

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("--batch-size must be at least 1.");
    }
}
