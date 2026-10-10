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
/// the size of the operand list. 1000-element <c>in</c> lists of numbers, long-key numbers, strings of any length and mixed
/// kinds finish well within the request timeout: the per-row cost does not grow with the operands' lengths (review
/// rounds 2 and 3), including when every row passes the long-operand prefilter and is confirmed exactly.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlFilterPerformanceTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    private const int Rows = 10_000;
    private const string Workspace = "performance";

    /// <summary>Every message has metadata <c>{"n": i, "tags": [i, i+1, i+2], "s": "x{i}", "big": Big(i)}</c>.</summary>
    private const string Session = "performance-session";

    /// <summary>
    /// Every message has metadata <c>{"h": Huge(i), "hh": Huge(i % 1000), "ls": LongString(i % 1000)}</c>: unique
    /// 1000-digit numbers, and 1000-digit numbers and 1999-unit strings each shared by ten rows, so a 1000-operand list
    /// can make every row a prefilter hit that must be confirmed exactly (the worst case).
    /// </summary>
    private const string LongSession = "performance-long";

    /// <summary>
    /// Every message's content and metadata <c>"r"</c> are <c>Repetitive(i)</c>: <c>'s'</c> x 1990 and five digits, the
    /// text on which <c>LIKE '%…%'</c> slows down quadratically (partner review I3).
    /// </summary>
    private const string RepetitiveSession = "performance-repetitive";

    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

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
                await store.Sessions.GetOrCreateAsync(Workspace, LongSession, null, null, null, Ct);
                await store.Sessions.GetOrCreateAsync(Workspace, RepetitiveSession, null, null, null, Ct);
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
                    await store.Messages.AppendAsync(
                        Workspace,
                        LongSession,
                        [.. Enumerable.Range(start, 1000).Select(i => new NewMessage(
                            "alice",
                            $"h{i}",
                            1,
                            new JsonObject { ["h"] = JsonNode.Parse(Huge(i)), ["hh"] = JsonNode.Parse(Huge(i % 1000)), ["ls"] = LongString(i % 1000) },
                            null))],
                        null,
                        Ct);
                    await store.Messages.AppendAsync(
                        Workspace,
                        RepetitiveSession,
                        [.. Enumerable.Range(start, 1000).Select(i => new NewMessage("alice", Repetitive(i), 1, new JsonObject { ["r"] = Repetitive(i) }, null))],
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

    /// <summary>A 1000-digit integer unique to <paramref name="i"/>.</summary>
    private static string Huge(int i) => new string('3', 994) + i.ToString("D5", CultureInfo.InvariantCulture) + "1";

    /// <summary>A 300-digit integer unique to <paramref name="i"/>, equal to no <see cref="Huge"/>.</summary>
    private static string ThreeHundredDigits(int i) => "1" + new string('7', 293) + i.ToString("D5", CultureInfo.InvariantCulture) + "1";

    private static string Repetitive(int i) => new string('s', 1990) + i.ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>A 1999-code-unit string unique to <paramref name="i"/>.</summary>
    private static string LongString(int i) => new string('L', 1994) + i.ToString("D5", CultureInfo.InvariantCulture);

    private static string List(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";

    private static string Quote(string value) => JsonValue.Create(value)!.ToJsonString();

    /// <summary><paramref name="count"/> conditions from <paramref name="leaf"/>, joined by <paramref name="combinator"/>.</summary>
    private static string Leaves(string combinator, int count, Func<int, string> leaf) =>
        "{\"" + combinator + "\":[" + string.Join(",", Enumerable.Range(0, count).Select(leaf)) + "]}";

    public static TheoryData<string, string, string, int, int, int> Filters()
    {
        var sevens = Enumerable.Range(0, 1000).Select(i => i * 7).ToList();
        var tens = Enumerable.Range(0, 1000).Select(i => i * 10).ToList();
        var multiplesOfThree = string.Join(",", Enumerable.Range(0, 1000).Select(i => (i * 3).ToString(CultureInfo.InvariantCulture)));
        var ten = string.Join(",", Enumerable.Range(0, 10).Select(i => (i * 7).ToString(CultureInfo.InvariantCulture)));
        return new TheoryData<string, string, string, int, int, int>
        {
            // Many conditions (partner review I2): each row's metadata is read once for all of them. Bounds are at least 4x the
            // worst cold-cache time seen. A condition repeated under many keys is tested once (1990 contains on distinct keys:
            // 0.55 s cold, 9.7 s before that change). The OR of 500 ANDs shares nothing and is mostly compile time: worst seen
            // 5.7 s cold, so its bound is 25 s, under the 30 s command timeout.
            { "or-1000-gt", Session, Leaves("OR", 1000, i => "{\"metadata\":{\"n\":{\"gt\":" + (Rows + i).ToString(CultureInfo.InvariantCulture) + "}}}"), 0, Rows, 12 },
            { "and-1000-ne", Session, Leaves("AND", 1000, i => "{\"metadata\":{\"n\":{\"ne\":" + (Rows + i).ToString(CultureInfo.InvariantCulture) + "}}}"), Rows, Rows, 5 },
            { "or-1000-contains-distinct-keys", Session, Leaves("OR", 1000, i => "{\"metadata\":{\"k" + i.ToString(CultureInfo.InvariantCulture) + "\":{\"contains\":\"zz\"}}}"), 0, 0, 5 },
            { "or-1990-contains-distinct-keys", Session, Leaves("OR", 1990, i => "{\"metadata\":{\"k" + i.ToString(CultureInfo.InvariantCulture) + "\":{\"contains\":\"zz\"}}}"), 0, 0, 5 },
            { "or-1000-gt-distinct-keys", Session, Leaves("OR", 1000, i => "{\"metadata\":{\"k" + i.ToString(CultureInfo.InvariantCulture) + "\":{\"gt\":5}}}"), 0, 0, 5 },
            { "or-500-and-pairs", Session, "{\"OR\":[" + string.Join(",", Enumerable.Range(0, 500).Select(i => "{\"metadata\":{\"n\":{\"gte\":0},\"s\":{\"contains\":\"zz" + i.ToString(CultureInfo.InvariantCulture) + "\"}}}")) + "]}", 0, Rows, 25 },
            { "or-128-gt", Session, Leaves("OR", 128, i => "{\"metadata\":{\"n\":{\"gt\":" + (Rows + i).ToString(CultureInfo.InvariantCulture) + "}}}"), 0, Rows, 6 },
            { "and-128-ne", Session, Leaves("AND", 128, i => "{\"metadata\":{\"n\":{\"ne\":" + (Rows + i).ToString(CultureInfo.InvariantCulture) + "}}}"), Rows, Rows, 8 },
            { "or-128-contains-distinct-keys", Session, Leaves("OR", 128, i => "{\"metadata\":{\"k" + i.ToString(CultureInfo.InvariantCulture) + "\":{\"contains\":\"zz\"}}}"), 0, 0, 5 },
            // Repetitive text (partner review I3): CHARINDEX, and the length check before any search.
            { "contains-1990-repetitive", RepetitiveSession, "{\"content\":{\"contains\":" + Quote(new string('s', 1989) + "x") + "}}", 0, 0, 5 },
            { "contains-1990-repetitive-hit", RepetitiveSession, "{\"content\":{\"contains\":" + Quote(new string('s', 1985) + "00042") + "}}", 1, 0, 5 },
            { "icontains-1990-repetitive", RepetitiveSession, "{\"content\":{\"icontains\":" + Quote(new string('S', 1989) + "X") + "}}", 0, 0, 5 },
            { "contains-4100-over-shorter-rows", RepetitiveSession, "{\"content\":{\"contains\":" + Quote(new string('s', 4100)) + "}}", 0, 0, 5 },
            { "metadata-icontains-1995-repetitive", RepetitiveSession, "{\"metadata\":{\"r\":{\"icontains\":" + Quote(new string('S', 1994) + "X") + "}}}", 0, 0, 5 },
            { "metadata-contains-1995-repetitive", RepetitiveSession, "{\"metadata\":{\"r\":{\"contains\":" + Quote(new string('s', 1994) + "x") + "}}}", 0, 0, 5 },

            // Shapes the merges cannot collapse: an OR of ANDs (each AND a number and a string condition on two keys, the
            // first true for every row), and an AND of positive conditions on the keys every row has.
            { "or-64-and-pairs", Session, "{\"OR\":[" + string.Join(",", Enumerable.Range(0, 64).Select(i => "{\"metadata\":{\"n\":{\"gte\":0},\"s\":{\"contains\":\"zz" + i.ToString(CultureInfo.InvariantCulture) + "\"}}}")) + "]}", 0, Rows, 6 },
            { "and-128-positive-two-keys", Session, Leaves("AND", 128, i => i % 2 == 0 ? "{\"metadata\":{\"n\":{\"gt\":" + (-1 - i).ToString(CultureInfo.InvariantCulture) + "}}}" : "{\"metadata\":{\"s\":{\"contains\":\"x\"}}}"), Rows, Rows, 5 },

            // name, session, filter, expected matches, the most key-function calls one statement may make, and the time
            // bound in seconds through the store (at least 4x the time measured in review round 3)
            { "in-1000-numbers", Session, "{\"metadata\":{\"n\":{\"in\":[" + multiplesOfThree + "]}}}", 1000, Rows, 5 },
            { "eq", Session, """{"metadata":{"n":300}}""", 1, Rows, 5 },
            { "eq-exponent", Session, """{"metadata":{"n":3e2}}""", 1, Rows, 5 },
            { "gt", Session, """{"metadata":{"n":{"gt":200}}}""", Rows - 201, Rows, 5 },
            { "range", Session, """{"metadata":{"n":{"gte":100,"lt":1.1e2}}}""", 10, Rows, 5 },
            { "array-contains-10", Session, "{\"metadata\":{\"tags\":[" + ten + "]}}", 0, 3 * Rows, 5 },
            { "array-contains-2", Session, """{"metadata":{"tags":[5,7]}}""", 1, 3 * Rows, 10 },

            // Long keys: packed as digests, tested only for rows whose own key is long. Over 1000-digit rows most of the time
            // is computing the stored keys (about 1.5 s for 10,000 such numbers), which any numeric filter on them pays.
            { "in-1000-long-keys-short-rows", Session, "{\"metadata\":{\"n\":{\"in\":" + List(sevens.Select(Big)) + "}}}", 0, Rows, 5 },
            { "in-1000-long-keys-long-rows", Session, "{\"metadata\":{\"big\":{\"in\":" + List(sevens.Select(Big)) + "}}}", 1000, Rows, 8 },
            { "in-500-long-500-short", Session, "{\"metadata\":{\"n\":{\"in\":" + List(sevens.Select(i => i % 2 == 0 ? Big(i) : i.ToString(CultureInfo.InvariantCulture))) + "}}}", 500, Rows, 5 },
            { "in-500-long-500-short-long-rows", Session, "{\"metadata\":{\"big\":{\"in\":" + List(sevens.Select(i => i % 2 == 0 ? Big(i) : i.ToString(CultureInfo.InvariantCulture))) + "}}}", 500, Rows, 10 },
            { "in-1000-1000-digit-short-rows", Session, "{\"metadata\":{\"n\":{\"in\":" + List(sevens.Select(Huge)) + "}}}", 0, Rows, 5 },
            { "in-1000-1000-digit-1000-digit-rows", LongSession, "{\"metadata\":{\"h\":{\"in\":" + List(sevens.Select(Huge)) + "}}}", 1000, Rows, 14 },
            { "in-1000-1000-digit-all-rows-hit", LongSession, "{\"metadata\":{\"hh\":{\"in\":" + List(Enumerable.Range(0, 1000).Select(Huge)) + "}}}", Rows, Rows, 14 },
            { "in-1000-1000-digit-same-length-misses", LongSession, "{\"metadata\":{\"hh\":{\"in\":" + List(Enumerable.Range(1000, 1000).Select(Huge)) + "}}}", 0, Rows, 14 },
            { "in-1000-300-digit-1000-digit-rows", LongSession, "{\"metadata\":{\"h\":{\"in\":" + List(sevens.Select(ThreeHundredDigits)) + "}}}", 0, Rows, 10 },

            // Strings: up to 16 units packed as hex, longer ones as digests; the per-row cost is independent of their length.
            { "in-1000-strings", Session, "{\"metadata\":{\"s\":{\"in\":" + List(sevens.Select(i => $"\"x{i}\"")) + "}}}", 1000, 0, 5 },
            { "in-500-strings-500-numbers", Session, "{\"metadata\":{\"s\":{\"in\":" + List(sevens.Select(i => i % 2 == 0 ? $"\"x{i}\"" : i.ToString(CultureInfo.InvariantCulture))) + "}}}", 500, Rows, 5 },
            { "in-500-numbers-500-strings", Session, "{\"metadata\":{\"n\":{\"in\":" + List(sevens.Select(i => i % 2 == 0 ? $"\"x{i}\"" : i.ToString(CultureInfo.InvariantCulture))) + "}}}", 500, Rows, 8 },
            { "in-1000-200-unit-strings-short-rows", Session, "{\"metadata\":{\"s\":{\"in\":" + List(sevens.Select(i => Quote(new string('x', 195) + i.ToString("D5", CultureInfo.InvariantCulture)))) + "}}}", 0, 0, 5 },
            { "in-1000-1999-unit-strings-short-rows", Session, "{\"metadata\":{\"s\":{\"in\":" + List(sevens.Select(i => Quote(LongString(i)))) + "}}}", 0, 0, 5 },
            { "in-1000-1999-unit-strings-all-rows-hit", LongSession, "{\"metadata\":{\"ls\":{\"in\":" + List(Enumerable.Range(0, 1000).Select(i => Quote(LongString(i)))) + "}}}", Rows, 0, 10 },
            { "in-100-1999-unit-strings-long-rows", LongSession, "{\"metadata\":{\"ls\":{\"in\":" + List(tens.Take(100).Select(i => Quote(LongString(i)))) + "}}}", 1000, 0, 8 },
            { "in-1000-1999-unit-strings-same-length-misses", LongSession, "{\"metadata\":{\"ls\":{\"in\":" + List(Enumerable.Range(1000, 1000).Select(i => Quote(LongString(i)))) + "}}}", 0, 0, 8 },
        };
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task MetadataFilter_ComputesKeysOncePerValue_AndIsFast(string name, string session, string filterJson, int matches, int maxCalls, int maxSeconds)
    {
        var database = await SeedAsync();
        var filter = FilterParser.Parse(filterJson, ResourceKind.Message)!;

        // One statement, as the store runs it: the call count must not grow with the operand list.
        var (count, calls, countTime) = await CountWithCallsAsync(database, session, filter);
        count.ShouldBe(matches);
        calls.ShouldBeLessThanOrEqualTo(maxCalls, $"{name}: key-function calls in one statement");

        // Through the store (count plus first page), timed.
        var store = database.CreateStore(TimeProvider.System);
        var watch = Stopwatch.StartNew();
        var page = await store.Messages.ListAsync(Workspace, session, filter, new PageRequest(1, 50), Ct);
        watch.Stop();
        output.WriteLine($"{name}: {watch.ElapsedMilliseconds} ms through the store, {countTime.TotalMilliseconds:F0} ms COUNT(*), {calls} key calls per statement, {page.Total} matches");

        page.Total.ShouldBe(matches);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(maxSeconds), name);
    }
    private static async Task<(int Count, long Calls, TimeSpan Time)> CountWithCallsAsync(SqlTestDatabase database, string session, FilterNode filter)
    {
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Message, "t");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);

        var before = await KeyCallsAsync(connection);
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM dbo.Messages AS t WHERE t.SessionId = (SELECT s.Id FROM dbo.Sessions AS s WHERE s.Name = @session) AND {where}",
            connection);
        command.Parameters.AddRange([.. parameters]);
        command.Parameters.Add(new SqlParameter("@session", System.Data.SqlDbType.NVarChar, 512) { Value = session });
        command.CommandTimeout = 120;
        var watch = Stopwatch.StartNew();
        var count = (int)(await command.ExecuteScalarAsync(Ct))!;
        watch.Stop();
        return (count, await KeyCallsAsync(connection) - before, watch.Elapsed);
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
