using Nachos.Cli;

namespace Nachos.Cli.Tests;

/// <summary>The result of running the CLI in-process with captured output.</summary>
internal sealed record CliRun(int ExitCode, string Out, string Error)
{
    public static async Task<CliRun> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApp.RunAsync(args, output, error, CancellationToken.None);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }
}
