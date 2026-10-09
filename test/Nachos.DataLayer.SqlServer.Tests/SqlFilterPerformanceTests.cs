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
/// Numeric metadata filters compute each stored number's order key once per row (Task 7 review C1): the key function's
/// call count per statement is bounded by the number of numeric values, whatever the size of the operand list, and a
/// 1000-number <c>in</c> over 10,000 messages finishes well within the request timeout.
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

    /// <summary>Every message has metadata <c>{"n": i, "tags": [i, i+1, i+2]}</c>: one number and three array numbers.</summary>
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
                            new JsonObject { ["n"] = i, ["tags"] = new JsonArray(i, i + 1, i + 2) },
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

    public static TheoryData<string, string, int, int> Filters()
    {
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
        };
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task NumericFilter_ComputesKeysOncePerValue_AndIsFast(string name, string filterJson, int matches, int maxCalls)
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
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), name);
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
