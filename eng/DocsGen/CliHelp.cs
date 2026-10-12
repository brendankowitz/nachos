using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Nachos.DocsGen;

internal static partial class CliHelp
{
    internal static string Dotnet => Path.GetFullPath(Path.Combine(
        RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    internal static async Task<SortedDictionary<string, string>> CaptureAsync(
        string assembly, string repository, TimeSpan timeout)
    {
        if (!File.Exists(assembly) || Path.GetExtension(assembly) != ".dll" ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new InvalidDataException();
        var home = Path.Combine(repository, "eng", "DocsGen", "obj", "help-" + Guid.NewGuid().ToString("N"));
        Inputs.RejectLinks(home);
        Directory.CreateDirectory(home);
        try
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var pending = new Queue<string[]>();
            pending.Enqueue([]);
            while (pending.TryDequeue(out var command))
            {
                if (result.Count >= 128 || command.Length > 8) throw new InvalidDataException();
                var text = await RunAsync(assembly, home, command, timeout);
                result.Add(string.Join(' ', command), text);
                foreach (var child in Children(text)) pending.Enqueue([.. command, child]);
            }
            return result;
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static async Task<string> RunAsync(string assembly, string home, string[] command, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(Dotnet)
        {
            WorkingDirectory = home,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Inputs.Utf8,
            StandardErrorEncoding = Inputs.Utf8,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(assembly);
        foreach (var token in command) start.ArgumentList.Add(token);
        start.ArgumentList.Add("--help");
        start.Environment.Clear();
        var dotnetRoot = Path.GetDirectoryName(Dotnet)!;
        start.Environment["PATH"] = dotnetRoot;
        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            start.Environment["SystemRoot"] = windows;
            start.Environment["WINDIR"] = windows;
            start.Environment["PATH"] += Path.PathSeparator + Path.Combine(windows, "System32");
        }
        foreach (var key in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "DOTNET_CLI_HOME", "TEMP", "TMP" })
            start.Environment[key] = home;
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["LANG"] = "en_US.UTF-8";
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(timeout);
        if (!process.Start()) throw new InvalidDataException();
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput, 131072, deadline);
        var stderr = ReadBoundedAsync(process.StandardError, 16384, deadline);
        try
        {
            // WaitForExit alone is not enough: an exited process may leave inherited pipes open.
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).WaitAsync(deadline.Token);
            if (process.ExitCode != 0 || (await stderr).Length != 0) throw new InvalidDataException();
            var text = (await stdout).ReplaceLineEndings("\n").TrimEnd() + "\n";
            if (!text.Contains("\nUsage:\n", StringComparison.Ordinal) ||
                !text.Contains("--help", StringComparison.Ordinal) || text.Contains("````", StringComparison.Ordinal) ||
                text.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
                throw new InvalidDataException();
            return text;
        }
        finally
        {
            await deadline.CancelAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            // Both waiting and redirected reads stay bounded even on cleanup.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { /* Disposal closes our pipe handles; the failure still propagates. */ }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationTokenSource deadline)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, deadline.Token);
            if (read == 0) return result.ToString();
            if (result.Length + read > limit)
            {
                await deadline.CancelAsync();
                throw new InvalidDataException();
            }
            result.Append(buffer, 0, read);
        }
    }

    private static IEnumerable<string> Children(string help)
    {
        var section = false;
        foreach (var line in help.Split('\n'))
        {
            if (line == "Commands:") { section = true; continue; }
            if (!section || line.Length == 0) continue;
            if (!line.StartsWith("  ", StringComparison.Ordinal)) break;
            var match = CommandLine().Match(line);
            if (!match.Success) throw new InvalidDataException();
            yield return match.Groups[1].Value;
        }
    }

    [GeneratedRegex(@"^  ([a-z][a-z0-9-]*)(?:, [a-z][a-z0-9-]*)*(?:\s{2,}.*)?$")]
    private static partial Regex CommandLine();
}
