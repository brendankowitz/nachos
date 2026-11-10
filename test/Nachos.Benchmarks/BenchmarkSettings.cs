using System.Globalization;

namespace Nachos.Benchmarks;

/// <summary>The command line: <c>[--scales 100000,250000,500000,1000000] [--queries 200] [--budget-seconds 60]
/// [--memory-gb 10] [--connection &lt;server connection string&gt;] [--out &lt;directory&gt;]</c>.</summary>
internal sealed record BenchmarkSettings(
    IReadOnlyList<long> Scales, int Queries, TimeSpan Budget, int MemoryGb, string? Connection, string OutputDirectory)
{
    public const int Seed = 20261109;

    /// <exception cref="ArgumentException">An option is unknown, lacks its value, or has a value out of range.</exception>
    public static BenchmarkSettings Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected '--option value' pairs; got '{args[i]}'.");
            }

            values[args[i][2..]] = args[i + 1];
        }

        var unknown = values.Keys.Except(["scales", "queries", "budget-seconds", "memory-gb", "connection", "out"]).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown)}.");
        }

        var scales = values.TryGetValue("scales", out var scaleText)
            ? scaleText.Split(',').Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToArray()
            : [100_000L, 250_000L, 500_000L, 1_000_000L];
        if (scales.Length == 0 || scales.Any(s => s < 1) || !scales.SequenceEqual(scales.Order()))
        {
            throw new ArgumentException("--scales must be positive and ascending: each step adds messages to the previous one.");
        }

        var settings = new BenchmarkSettings(
            scales,
            Int(values, "queries", 200),
            TimeSpan.FromSeconds(Int(values, "budget-seconds", 60)),
            Int(values, "memory-gb", 10),
            values.GetValueOrDefault("connection"),
            values.GetValueOrDefault("out") ?? Path.Combine("artifacts", "benchmarks"));
        return settings.Queries < 1 || settings.MemoryGb < 4
            ? throw new ArgumentException("--queries must be at least 1 and --memory-gb at least 4.")
            : settings;
    }

    private static int Int(Dictionary<string, string> values, string name, int fallback) =>
        values.TryGetValue(name, out var text) ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
}
