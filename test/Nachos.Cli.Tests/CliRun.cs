using System.Diagnostics;
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

    /// <summary>
    /// Runs the built CLI as a separate process, exactly as an operator or a hook would, so that nothing in the real startup path
    /// (<c>Program.cs</c>, the console streams) can leak what the in-process runs hide.
    /// </summary>
    public static async Task<CliRun> RunProcessAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Nachos.Cli.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return new CliRun(process.ExitCode, await output, await error);
    }
}
