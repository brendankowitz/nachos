using System.Diagnostics;

namespace Nachos.Cli.Tests;

/// <summary>Runs <c>dotnet</c> as a child process with both output streams captured, and never waits longer than a fixed bound.</summary>
internal static class DotnetProcess
{
    /// <summary>
    /// Runs <c>dotnet <paramref name="arguments"/></c> and returns its exit code and the raw bytes of both streams.
    /// </summary>
    /// <remarks>
    /// A process that exits does not close the pipes by itself: any descendant that inherited them (an MSBuild worker node kept
    /// alive for reuse, a compiler server) holds them open, and reading to the end then waits for that descendant instead. So the
    /// exit and both reads share one <paramref name="timeout"/>, MSBuild node reuse is off for everything started here, and on timeout
    /// the process tree is killed and <see cref="TimeoutException"/> is thrown, so a test fails instead of hanging the run.
    /// </remarks>
    public static async Task<(int ExitCode, byte[] Out, byte[] Error)> RunAsync(
        IEnumerable<string> arguments, TimeSpan timeout, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(timeout);
        var output = ReadAllBytesAsync(process.StandardOutput.BaseStream, deadline.Token);
        var error = ReadAllBytesAsync(process.StandardError.BaseStream, deadline.Token);
        try
        {
            // WaitAsync bounds the reads even where a blocking pipe read does not observe the token itself.
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Kills the process and what is still in its tree; that also closes the pipe ends they hold. A descendant whose parent
            // has already exited is outside the tree, which is why node reuse is off rather than relying on this.
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"'dotnet {string.Join(' ', start.ArgumentList)}' did not exit and close its output within {timeout}.");
        }

        return (process.ExitCode, await output, await error);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
