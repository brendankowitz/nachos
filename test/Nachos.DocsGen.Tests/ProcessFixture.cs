using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nachos.DocsGen.Tests;

internal static class ProcessFixture
{
    public static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--child" }, StringComparer.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            return 0;
        }
        // A unit-only executable. It refuses anything except help; it has no product operations.
        if (!args.SequenceEqual(new[] { "--help" }, StringComparer.Ordinal)) return 18;
        var assembly = Assembly.GetExecutingAssembly().Location;
        var mode = Path.GetFileNameWithoutExtension(assembly);
        if (mode == "nonzero")
        {
            Console.Error.WriteLine("synthetic-secret-must-not-leak");
            return 17;
        }
        if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan);
        if (mode == "flood") Console.Write(new string('x', 200000));
        if (mode == "stderr")
        {
            Console.Error.WriteLine("synthetic-secret-must-not-leak");
        }
        if (mode == "pipes")
        {
            var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            using var child = Process.Start(new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false,
                ArgumentList = { assembly, "--child" },
            })!;
            File.WriteAllText(assembly + ".pid", child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (mode == "environment")
        {
            if (Environment.GetEnvironmentVariable("DOCSGEN_SYNTHETIC_SECRET") is not null ||
                Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") is not null ||
                Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") is not null ||
                Environment.GetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE") != "false" ||
                Environment.GetEnvironmentVariable("HOME") != Environment.CurrentDirectory ||
                Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE") != "1") return 19;
        }
        Console.Write("Description:\n  First-party process test.\n\nUsage:\n  fixture [options]\n\nOptions:\n  --help  Show help\n");
        return 0;
    }
}
