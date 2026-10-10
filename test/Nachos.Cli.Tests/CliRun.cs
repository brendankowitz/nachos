using System.Text;
using Nachos.Cli;

namespace Nachos.Cli.Tests;

/// <summary>The result of running the CLI with captured output.</summary>
internal sealed record CliRun(int ExitCode, string Out, string Error)
{
    /// <summary>Runs in this process, with the streams captured.</summary>
    public static async Task<CliRun> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApp.RunAsync(args, output, error, CancellationToken.None);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    // Redirected output is promised as UTF-8 without a byte order mark, so anything else must fail the test that reads it.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Runs the built CLI as a separate process, exactly as an operator or a hook would, so that nothing in the real startup path
    /// (<c>Program.cs</c>, the console streams) can leak what the in-process runs hide. Both streams are read as raw bytes and
    /// decoded as strict UTF-8; a byte order mark is kept, so it shows up as a leading U+FEFF.
    /// </summary>
    public static async Task<CliRun> RunProcessAsync(params string[] args)
    {
        var (exitCode, output, error) = await DotnetProcess.RunAsync(
            [Path.Combine(AppContext.BaseDirectory, "Nachos.Cli.dll"), .. args], TimeSpan.FromMinutes(2));
        return new CliRun(exitCode, StrictUtf8.GetString(output), StrictUtf8.GetString(error));
    }
}
