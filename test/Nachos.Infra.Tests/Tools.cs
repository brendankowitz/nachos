using System.Diagnostics;

namespace Nachos.Infra.Tests;

/// <summary>Result of running an external process to completion.</summary>
internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Locates and runs the offline tooling these tests depend on (bicep, az, bash, pwsh).
/// Nothing here may touch Azure: callers only pass offline sub-commands.
/// </summary>
internal static class Tools
{
    private const string RequireToolsVariable = "NACHOS_REQUIRE_INFRA_TOOLS";

    /// <summary>
    /// CI sets <c>NACHOS_REQUIRE_INFRA_TOOLS=1</c> so a missing tool fails the run instead of
    /// silently skipping it.
    /// </summary>
    public static bool ToolsAreRequired =>
        string.Equals(Environment.GetEnvironmentVariable(RequireToolsVariable), "1", StringComparison.Ordinal);

    /// <summary>Returns the full path of the first of <paramref name="names"/> found on PATH, or null.</summary>
    public static string? Find(params string[] names)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", string.Empty } : new[] { string.Empty };

        foreach (var name in names)
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Like <see cref="Find"/> but throws with an actionable message when nothing is found.</summary>
    public static string Require(params string[] names) =>
        Find(names) ?? throw new InvalidOperationException(
            $"None of [{string.Join(", ", names)}] is on PATH. Install one of them or unset {RequireToolsVariable}.");

    public static ProcessResult Run(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");

        // Drain both pipes concurrently so a chatty tool cannot deadlock on a full buffer.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{fileName}' did not finish within 2 minutes.");
        }

        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
