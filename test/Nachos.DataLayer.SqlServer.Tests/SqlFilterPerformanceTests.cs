using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.DataLayer.SqlServer.Filtering;
using Shouldly;
using Xunit.Abstractions;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Metadata filters stay set-based over 10,000 messages. Numeric filters compute each stored number's order key once per
/// row (Task 7 review C1): the key function's call count per statement is bounded by the number of numeric values, whatever
/// the size of the operand list. 1000-element <c>in</c> lists of numbers, long-key numbers (review round 2, I-1), strings
/// and mixed kinds finish well within the request timeout.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlFilterPerformanceTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    private const int Rows = 10_000;
    private const string Workspace = "performance";
    private const string Session = "performance-session";

    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    /// <summary>
/// Every message has metadata <c>{"n": i, "tags": [i, i+1, i+2], "s": "x{i}", "big": Big(i)}</c>: numbers, array
/// numbers, a string and a number whose order key is longer than the short-key limit.
/// </summary>
    private async Task<SqlTestDatabase> SeedAsync()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "performance");
        await SeedLock.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                var store = database.CreateStore(TimeProvider.System);
                await store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
                await store.Sessions.GetOrCreateAsync(Workspace, Session, null, null, null, Ct);
                for (var start = 0; start < Rows; start += 1000)
                {
                    await store.Messages.AppendAsync(
                        Workspace,
                        Session,
                        [.. Enumerable.Range(start, 1000).Select(i => new NewMessage(
                            "alice",
                            $"m{i}",
                            1,
                            new JsonObject { ["n"] = i, ["tags"] = new JsonArray(i, i + 1, i + 2), ["s"] = $"x{i}", ["big"] = JsonNode.Parse(Big(i)) },
                            null))],
                        null,
                        Ct);
                }

                _seeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }

        return database;
    }

    /// <summary>A 98-significant-digit number unique to <paramref name="i"/>: its order key is over 100 characters.</summary>
    private static string Big(int i) => "1." + new string('3', 90) + i.ToString("D5", CultureInfo.InvariantCulture) + "1";

    private static string List(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";

    public static TheoryData<string, string, int, int> Filters()
    {
        var sevens = Enumerable.Range(0, 1000).Select(i => i * 7).ToList();
        var multiplesOfThree = string.Join(",", Enumerable.Range(0, 1000).Select(i => (i * 3).ToString(CultureInfo.InvariantCulture)));
        var ten = string.Join(",", Enumerable.Range(0, 10).Select(i => (i * 7).ToString(CultureInfo.InvariantCulture)));
        return new TheoryData<string, string, int, int>
        {
            // name, filter, expected matches, the most key-function calls one statement may make
            { "in-1000-numbers", "{\"metadata\":{\"n\":{\"in\":[" + multiplesOfThree + "]}}}", 1000, Rows },
            { "eq", """{"metadata":{"n":300}}""", 1, Rows },
            { "eq-exponent", """{"metadata":{"n":3e2}}""", 1, Rows },
            { "gt", """{"metadata":{"n":{"gt":200}}}""", Rows - 201, Rows },
            { "range", """{"metadata":{"n":{"gte":100,"lt":1.1e2}}}""", 10, Rows },
            { "array-contains-10", "{\"metadata\":{\"tags\":[" + ten + "]}}", 0, 3 * Rows },
            { "array-contains-2", """{"metadata":{"tags":[5,7]}}""", 1, 3 * Rows },

            // Long keys (I-1): the long-key list is only parsed for rows whose own key is long.
            { "in-1000-long-keys-short-rows", "{\"metadata\":{\"n\":{\"in\":" + List(sevens.Select(Big)) + "}}}", 0, Rows },
            // Every row and every operand has a 98-digit number: each row scans the ~15 packed long-key chunks. Measured 6.0 s
            // (65 s before packing); the one case allowed more than 5 s, parked with the coordinator.
            { "in-1000-long-keys-long-rows", "{\"metadata\":{\"big\":{\"in\":" + List(sevens.Select(Big)) + "}}}", 1000, Rows },
            { "in-500-long-500-short", "{\"metadata\":{\"n\":{\"in\":" + List(sevens.Select(i => i % 2 == 0 ? Big(i) : i.ToString(CultureInfo.InvariantCulture))) + "}}}", 500, Rows },
            { "in-500-long-500-short-long-rows", "{\"metadata\":{\"big\":{\"in\":" + List(sevens.Select(i => i % 2 == 0 ? Big(i) : i.ToString(CultureInfo.InvariantCulture))) + "}}}", 500, Rows },

            // Strings (round 2, minor 1): packed hex of the UTF-16 code units, no per-row list parsing.
            { "in-1000-strings", "{\"metadata\":{\"s\":{\"in\":" + List(sevens.Select(i => $"\"x{i}\"")) + "}}}", 1000, 0 },
            { "in-500-strings-500-numbers", "{\"metadata\":{\"s\":{\"in\":" + List(sevens.Select(i => i % 2 == 0 ? $"\"x{i}\"" : i.ToString(CultureInfo.InvariantCulture))) + "}}}", 500, Rows },
            { "in-500-numbers-500-strings", "{\"metadata\":{\"n\":{\"in\":" + List(sevens.Select(i => i % 2 == 0 ? $"\"x{i}\"" : i.ToString(CultureInfo.InvariantCulture))) + "}}}", 500, Rows },
        };
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task MetadataFilter_ComputesKeysOncePerValue_AndIsFast(string name, string filterJson, int matches, int maxCalls)
    {
        var database = await SeedAsync();
        var filter = FilterParser.Parse(filterJson, ResourceKind.Message)!;

        // One statement, as the store runs it: the call count must not grow with the operand list.
        var (count, calls) = await CountWithCallsAsync(database, filter);
        count.ShouldBe(matches);
        calls.ShouldBeLessThanOrEqualTo(maxCalls, $"{name}: key-function calls in one statement");

        // Through the store (count plus first page), timed.
        var store = database.CreateStore(TimeProvider.System);
        var watch = Stopwatch.StartNew();
        var page = await store.Messages.ListAsync(Workspace, Session, filter, new PageRequest(1, 50), Ct);
        watch.Stop();
        output.WriteLine($"{name}: {watch.ElapsedMilliseconds} ms, {calls} key calls per statement, {page.Total} matches");

        page.Total.ShouldBe(matches);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(name == "in-1000-long-keys-long-rows" ? 15 : 5), name);
    }

    private static async Task<(int Count, long Calls)> CountWithCallsAsync(SqlTestDatabase database, FilterNode filter)
    {
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Message, "t");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);

        var before = await KeyCallsAsync(connection);
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM dbo.Messages AS t WHERE {where}", connection);
        command.Parameters.AddRange([.. parameters]);
        command.CommandTimeout = 60;
        var count = (int)(await command.ExecuteScalarAsync(Ct))!;
        return (count, await KeyCallsAsync(connection) - before);
    }

    /// <summary>Executions of both key functions in this database, from <c>sys.dm_exec_function_stats</c>.</summary>
    private static async Task<long> KeyCallsAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand(
            """
            SELECT ISNULL(SUM(execution_count), 0) FROM sys.dm_exec_function_stats
            WHERE database_id = DB_ID() AND object_id IN (OBJECT_ID(N'dbo.JsonNumberOrderKey'), OBJECT_ID(N'dbo.JsonNumberOrderKeyLong'))
            """,
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }
}
