using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Nachos.Benchmarks;

/// <summary>
/// Runs one query series sequentially (one query at a time, warm cache after two warm-up queries) until it has run
/// <see cref="BenchmarkSettings.Queries"/> queries or spent <see cref="BenchmarkSettings.Budget"/>, but at least
/// <see cref="MinimumQueries"/>. A command timeout counts as a failure and is left out of the percentiles; any other error
/// stops the benchmark.
/// </summary>
internal sealed class SeriesRunner(BenchmarkSettings settings, Probes probes)
{
    private const int WarmUpQueries = 2;
    private const int MinimumQueries = 10;

    public async Task<SeriesResult> RunAsync(string name, Func<int, CancellationToken, Task> query, CancellationToken ct)
    {
        Console.WriteLine($"  {name} ...");
        await probes.ClearPlanCacheAsync(ct);
        var failures = 0;
        for (var i = 0; i < WarmUpQueries; i++)
        {
            failures += await TimeAsync(query, settings.Queries + i, ct) is null ? 1 : 0;
        }

        var times = new List<double>();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < settings.Queries && (i < MinimumQueries || clock.Elapsed < settings.Budget); i++)
        {
            if (await TimeAsync(query, i, ct) is { } elapsed)
            {
                times.Add(elapsed);
            }
            else
            {
                failures++;
            }
        }

        var result = new SeriesResult(name, times.Count, failures, Percentile(times, 0.50), Percentile(times, 0.95),
            times.Count == 0 ? double.NaN : times.Max(), await probes.SlowestStatementAsync(ct));
        Console.WriteLine($"    n={result.Count} p50={result.P50Ms:N1} ms p95={result.P95Ms:N1} ms failures={result.Failures}");
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

    /// <summary>Nearest-rank percentile.</summary>
    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1)];
    }
}

internal sealed record SeriesResult(
    string Name, int Count, int Failures, double P50Ms, double P95Ms, double MaxMs, StatementStats? Statement);
