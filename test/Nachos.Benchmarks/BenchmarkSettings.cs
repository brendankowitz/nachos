using System.Globalization;

namespace Nachos.Benchmarks;

/// <summary>The command line; see README.md for the options.</summary>
/// <param name="SearchBudget">The time budget of the <c>W/search</c> and <c>S/search</c> series, which should reach
/// <see cref="Queries"/>.</param>
/// <param name="Budget">The time budget of every other series.</param>
/// <param name="VectorCaps">Extra store-level workspace vector series, one per candidate cap.</param>
internal sealed record BenchmarkSettings(
    IReadOnlyList<long> Scales,
    IReadOnlyList<int> VectorCaps,
    int Queries,
    TimeSpan SearchBudget,
    TimeSpan Budget,
    int MemoryGb,
    string? Connection,
    string OutputDirectory)
{
    public const int Seed = 20261109;

    /// <exception cref="ArgumentException">
    /// An option is unknown, lacks its value, is not a number where one is expected, or is out of range.
    /// </exception>
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

        string[] known = ["scales", "vector-caps", "queries", "search-budget-seconds", "budget-seconds", "memory-gb", "connection", "out"];
        var unknown = values.Keys.Except(known).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown)}.");
        }

        long[] scales = values.TryGetValue("scales", out var scaleText)
            ? [.. scaleText.Split(',').Select(text => ParseLong("scales", text))]
            : [100_000, 250_000, 500_000, 1_000_000];
        if (scales.Length == 0 || scales.Any(s => s < 1) || !scales.SequenceEqual(scales.Order()))
        {
            throw new ArgumentException("--scales must be positive and ascending: each step adds messages to the previous one.");
        }

        var settings = new BenchmarkSettings(
            scales,
            values.TryGetValue("vector-caps", out var capText)
                ? [.. capText.Split(',').Select(text => ParseInt("vector-caps", text))]
                : [],
            ParseInt(values, "queries", 200),
            TimeSpan.FromSeconds(ParseInt(values, "search-budget-seconds", 3_600)),
            TimeSpan.FromSeconds(ParseInt(values, "budget-seconds", 60)),
            ParseInt(values, "memory-gb", 12),
            values.GetValueOrDefault("connection"),
            values.GetValueOrDefault("out") ?? Path.Combine("artifacts", "benchmarks"));
        return settings.Queries < 1 || settings.MemoryGb < 6 || settings.VectorCaps.Any(cap => cap < 1)
            ? throw new ArgumentException("--queries and every --vector-caps value must be at least 1, and --memory-gb at least 6.")
            : settings;
    }

    private static int ParseInt(Dictionary<string, string> values, string name, int fallback) =>
        !values.TryGetValue(name, out var text) ? fallback
        : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
        : throw new ArgumentException($"--{name} must be a whole number; got '{text}'.");

    private static int ParseInt(string name, string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"--{name} must be whole numbers; got '{text}'.");

    private static long ParseLong(string name, string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"--{name} must be whole numbers; got '{text}'.");
}
