using System.CommandLine;
using Shouldly;

namespace Nachos.Cli.Tests;

public sealed class UsageErrorTests
{
    [Fact]
    public void TheExemptMessages_AreExactlyTheFixedUsageErrors()
    {
        // Anything added here is exempt from parser-error redaction, so a new entry must be reviewed: it must never contain a value.
        UsageErrors.Messages.ShouldBe(
        [
            "--allow-data-loss is only valid together with --approve-reviewed.",
            "--batch-size must be at least 1.",
            "--model must be 1-128 characters, without leading or trailing whitespace.",
            "The model must be one of: bge-small-en-v1.5, all-minilm-l6-v2.",
            "--object-id must not be empty.",
            "--object-id must not start or end with whitespace.",
            "--object-id must be at most 64 characters.",
            "--role must be one of: Nachos.Admin, Nachos.Workspace.",
            "--workspace must be 1–512 ASCII letters, digits, '_' or '-'.",
            "--workspace applies only to the Nachos.Workspace role; Nachos.Admin is not scoped to a workspace.",
        ]);
        UsageErrors.Messages.Distinct().Count().ShouldBe(UsageErrors.Messages.Count);
        UsageErrors.IsOwn("Unrecognized command or argument 'x'").ShouldBeFalse();
    }

    [Fact]
    public async Task AValidatorThatThrows_IsExit1_WithAFixedMessage_AndNoExceptionText()
    {
        var value = new Option<string>("--value");
        var command = new Command("probe") { value };
        command.Validators.Add(_ => throw new InvalidOperationException("secret-detail at C:\\build\\path\\Source.cs"));
        var root = new RootCommand { command };
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await CliApp.RunAsync(root, ["probe", "--value", "x"], output, error, CancellationToken.None);

        exitCode.ShouldBe(1);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("could not be processed");
        error.ToString().ShouldNotContain("secret-detail");
        error.ToString().ShouldNotContain("Source.cs");
    }
}
