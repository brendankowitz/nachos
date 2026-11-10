using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Nachos.Benchmarks;

/// <summary>
/// Runs one query series sequentially, after two warm-up queries, until it has run <see cref="BenchmarkSettings.Queries"/>
/// queries or spent its budget, but at least <see cref="MinimumQueries"/>. A command timeout counts in the percentiles as
/// <see cref="TimeoutMs"/>, the provider's 30 s command timeout, so a series that times out cannot look fast; any other
/// error stops the benchmark.
/// </summary>
internal sealed class SeriesRunner(BenchmarkSettings settings, Probes probes)
{
    public const double TimeoutMs = 30_000;

    /// <summary>Below this many measured queries a series is reported as indicative only.</summary>
    public const int IndicativeBelow = 50;

    private const int WarmUpQueries = 2;
    private const int MinimumQueries = 10;

    public async Task<SeriesResult> RunAsync(
        string name, TimeSpan budget, Func<int, CancellationToken, Task> query, CancellationToken ct)
    {
        Console.WriteLine($"  {name} ...");
        await probes.ClearPlanCacheAsync(ct);
        for (var i = 0; i < WarmUpQueries; i++)
        {
            await TimeAsync(query, settings.Queries + i, ct);
        }

        var times = new List<double>();
        var timeouts = 0;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < settings.Queries && (i < MinimumQueries || clock.Elapsed < budget); i++)
        {
            var elapsed = await TimeAsync(query, i, ct);
            timeouts += elapsed is null ? 1 : 0;
            times.Add(elapsed ?? TimeoutMs);
        }

        var result = new SeriesResult(name, times.Count, timeouts, Percentile(times, 0.50), Percentile(times, 0.95),
            times.Max(), await probes.SlowestStatementAsync(ct));
        Console.WriteLine($"    n={result.Count} timeouts={result.Timeouts} p50={result.P50Ms:N1} ms p95={result.P95Ms:N1} ms" +
            (result.IsIndicative ? " (indicative only)" : ""));
        return result;
    }

    private static async Task<double?> TimeAsync(Func<int, CancellationToken, Task> query, int index, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await query(index, ct);
            return clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            return null;
        }
    }

    private static bool IsTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: -2 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Nearest-rank percentile of a non-empty list.</summary>
    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1)];
    }
}

/// <param name="Count">Measured queries, timeouts included.</param>
/// <param name="Timeouts">Queries that hit the command timeout, counted as <see cref="SeriesRunner.TimeoutMs"/>.</param>
internal sealed record SeriesResult(
    string Name, int Count, int Timeouts, double P50Ms, double P95Ms, double MaxMs, StatementStats? Statement)
{
    public bool IsIndicative => Count < SeriesRunner.IndicativeBelow;
}
