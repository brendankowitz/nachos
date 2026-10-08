using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Differential tests for the filter semantics the shared cases do not reach: the same rows are stored in the
/// in-memory provider (the executable reference) and in SQL Server, and every filter must select the same rows from
/// both. The inputs target SQL's known divergences from .NET: space padding in <c>=</c>/<c>&lt;</c>, UTF-16 ordinal
/// order against supplementary characters, control characters below space, exact numbers beyond <c>decimal</c> and
/// <c>double</c>, JSON kinds, <c>UPPER</c> folding, <c>LIKE</c> metacharacters and operands longer than a
/// <c>LIKE</c> pattern may be.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlFilterDifferentialTests(SqlServerFixture fixture)
{
    private const string Workspace = "differential";
    private const string Session = "differential-session";
    private const string Max38 = "99999999999999999999999999999999999999";

    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly InMemoryMemoryStore Reference = new(TimeProvider.System);
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    private static readonly string LongNeedle = new string('x', 4100) + "needle";

    /// <summary>Stored string values: each one is the <c>k</c> metadata value of a peer and the content of a message.</summary>
    private static readonly string[] Strings =
    [
        "", " ", "a", "a ", "a  ", "a\t", "a\u0001", "a\u0000", "ab", "b", "A", "é", "É", "\uE000", "\uFFFF", "😀", "😀x",
        "\uD83D\uDE01", "z", "ok", "ok ", "%_[", "50%", LongNeedle, "pre" + new string('y', 5000),
    ];

    /// <summary>Stored number literals (all within the json column's exact domain).</summary>
    private static readonly string[] Numbers =
    [
        "0", "1", "1.0", "1.5", "-1", "-1.5", "100", "1e2", "0.25", "79228162514264337593543950336",
        "79228162514264337593543950337", "0.00000000000000000000000000001", "0.00000000000000000000000000000000000001",
        Max38, "-" + Max38, "-0.5", "-0", "0.1",
    ];

    private static readonly string[] OtherValues =
    [
        "true", "false", "null", """{"x":1}""", """["a","b",1,true]""", """[1.0,2,79228162514264337593543950336]""",
        """["A","ok "]""", "[]", """[[1],{"a":1}]""",
    ];

    private static readonly string[] StringOperands =
    [
        "", " ", "a", "a ", "a\t", "a\u0000", "ab", "A", "é", "\uE000", "\uFFFF", "😀", "\uD83D\uDE01", "ok", "ok ",
        "%", "_", "[", "%_[", "x", "X", LongNeedle, new string('x', 4100), new string('X', 4100) + "NEEDLE", "y",
    ];

    private static readonly string[] NumberOperands =
    [
        "0", "-0", "1", "1.0", "1e0", "10e-1", "1e2", "100.0", "1.5", "-1", "0.25", "2.5e-1",
        "79228162514264337593543950336", "79228162514264337593543950337", "7.9228162514264337593543950336e28",
        "1e-29", "1e-38", "1e-39", "1e-40", "1e40", "-1e40", "1e-400", "1e400", Max38, "1e38", "-" + Max38, "0.1",
    ];

    public static TheoryData<string, string> Filters()
    {
        var data = new TheoryData<string, string>();
        void Add(ResourceKind kind, JsonObject filter) => data.Add(kind.ToString(), filter.ToJsonString());
        JsonObject Meta(JsonNode? value) => new() { ["metadata"] = new JsonObject { ["k"] = value } };
        JsonObject MetaOp(string op, JsonNode? value) => Meta(new JsonObject { [op] = value });

        foreach (var text in StringOperands)
        {
            Add(ResourceKind.Peer, Meta(text));
            foreach (var op in new[] { "ne", "gt", "gte", "lt", "lte", "contains", "icontains" })
            {
                Add(ResourceKind.Peer, MetaOp(op, text));
            }

            Add(ResourceKind.Peer, Meta(new JsonArray(text)));
            Add(ResourceKind.Message, new JsonObject { ["content"] = text });
            foreach (var op in new[] { "ne", "contains", "icontains" })
            {
                Add(ResourceKind.Message, new JsonObject { ["content"] = new JsonObject { [op] = text } });
            }

            Add(ResourceKind.Message, new JsonObject { ["content"] = new JsonArray(text, "zzz") });
        }

        foreach (var literal in NumberOperands)
        {
            Add(ResourceKind.Peer, Meta(JsonNode.Parse(literal)));
            foreach (var op in new[] { "ne", "gt", "gte", "lt", "lte" })
            {
                Add(ResourceKind.Peer, MetaOp(op, JsonNode.Parse(literal)));
            }

            Add(ResourceKind.Peer, Meta(new JsonArray(JsonNode.Parse(literal))));
            Add(ResourceKind.Peer, MetaOp("in", new JsonArray(JsonNode.Parse(literal), "a", true)));
        }

        foreach (var flag in new[] { true, false })
        {
            Add(ResourceKind.Peer, Meta(flag));
            Add(ResourceKind.Peer, MetaOp("ne", flag));
            Add(ResourceKind.Peer, Meta(new JsonArray(flag)));
        }

        Add(ResourceKind.Peer, Meta(null));
        Add(ResourceKind.Peer, Meta("*"));
        Add(ResourceKind.Peer, MetaOp("ne", null));
        Add(ResourceKind.Peer, MetaOp("in", new JsonArray("a", null, 1)));
        Add(ResourceKind.Peer, Meta(new JsonArray(1, 2)));
        Add(ResourceKind.Peer, Meta(new JsonArray("a", "b", 1, true)));
        Add(ResourceKind.Peer, Meta(new JsonArray("a", "a")));
        Add(ResourceKind.Peer, new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["x"] = 1 } } });

        foreach (var key in new[] { "a.b", "q\"x", "k[0]", "o'b", "%_", "$", "", " ", "a ", "😀", "\u0000" })
        {
            Add(ResourceKind.Peer, new JsonObject { ["metadata"] = new JsonObject { [key] = 1 } });
            Add(ResourceKind.Peer, new JsonObject { ["metadata"] = new JsonObject { [key] = "*" } });
            Add(ResourceKind.Peer, new JsonObject { ["metadata"] = new JsonObject { [key] = new JsonObject { ["ne"] = 1 } } });
        }

        Add(ResourceKind.Peer, new JsonObject { ["metadata"] = new JsonObject { ["a"] = new JsonObject { ["b"] = 6 } } });
        Add(ResourceKind.Peer, new JsonObject { ["metadata"] = new JsonObject { ["arr"] = new JsonObject { ["0"] = 1 } } });
        return data;
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task SqlMatchesTheReference(string resource, string filterJson)
    {
        var store = await SeedAsync();
        var kind = Enum.Parse<ResourceKind>(resource);
        var filter = FilterParser.Parse(filterJson, kind);

        var expected = await ListAsync(Reference, kind, filter);
        var actual = await ListAsync(store, kind, filter);

        actual.ShouldBe(expected, ignoreOrder: true, $"{resource} {filterJson}");
    }

    /// <summary>
    /// Hand-computed results, so the differential cases cannot pass vacuously (for instance with nothing seeded). Each
    /// one fails under a known SQL pitfall: space padding, code-point (not UTF-16) order, approximate numbers.
    /// </summary>
    [Fact]
    public async Task Sentinels_HaveTheExactExpectedResults()
    {
        var store = await SeedAsync();

        // Trailing space is significant, and a proper prefix orders first even before control characters.
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":"a "}}"""))).ShouldBe(["p003"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":"a","lt":"ab"}}}"""))).ShouldBe(["p003", "p004", "p005", "p006", "p007"], ignoreOrder: true);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"lt":"a"}}}"""))).ShouldBe(["p000", "p001", "p010", "p021", "p022"], ignoreOrder: true);

        // UTF-16 order: U+FFFF and U+E000 sort after the surrogates of U+1F600 (code-point order would invert this).
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":"\uE000"}}}"""))).ShouldBe(["p014"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":"\uD83D\uDE00","lt":"\uE000"}}}"""))).ShouldBe(["p016", "p017"], ignoreOrder: true);

        // Exact numbers: 2^96 + 1 is distinct from 2^96, 1 equals 1.0, 1e2 equals 100, and 1e-29 is not 0.
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":79228162514264337593543950337}}"""))).ShouldBe(["p035"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":1}}"""))).ShouldBe(["p026", "p027"], ignoreOrder: true);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":100.0}}"""))).ShouldBe(["p031", "p032"], ignoreOrder: true);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":0,"lt":1e-28}}}"""))).ShouldBe(["p036", "p037"], ignoreOrder: true);

        // An operand longer than a LIKE pattern may be still matches exactly.
        (await ListAsync(store, ResourceKind.Message, Parse(new JsonObject { ["content"] = new JsonObject { ["contains"] = new string('x', 4100) + "need" } }.ToJsonString())))
            .ShouldBe(["23"]);

        static FilterNode? Parse(string json) => FilterParser.Parse(json, json.Contains("content", StringComparison.Ordinal) ? ResourceKind.Message : ResourceKind.Peer);
    }

    private static async Task<List<string>> ListAsync(IMemoryStore store, ResourceKind kind, FilterNode? filter)
    {
        var ids = new List<string>();
        if (kind == ResourceKind.Peer)
        {
            await foreach (var peer in ((Func<PageRequest, CancellationToken, Task<Page<PeerRecord>>>)((page, ct) =>
                store.Peers.ListAsync(Workspace, PeerKind.All, filter, page, ct))).EnumerateAsync(100))
            {
                ids.Add(peer.Name);
            }
        }
        else
        {
            await foreach (var message in ((Func<PageRequest, CancellationToken, Task<Page<MessageRecord>>>)((page, ct) =>
                store.Messages.ListAsync(Workspace, Session, filter, page, ct))).EnumerateAsync(100))
            {
                // Public ids differ between the stores; the content index identifies the message in both.
                ids.Add(message.Metadata["i"]!.ToJsonString());
            }
        }

        return ids;
    }

    private async Task<SqlMemoryStore> SeedAsync()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "filter-differential");
        var store = database.CreateStore(TimeProvider.System);
        await SeedLock.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                await SeedAsync(Reference);
                await SeedAsync(store);
                _seeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }

        return store;
    }

    private static async Task SeedAsync(IMemoryStore store)
    {
        await store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(Workspace, Session, null, null, null, Ct);

        var values = Strings.Select(s => (JsonNode?)JsonValue.Create(s))
            .Concat(Numbers.Select(n => JsonNode.Parse(n)))
            .Concat(OtherValues.Select(v => JsonNode.Parse(v)))
            .ToList();
        for (var i = 0; i < values.Count; i++)
        {
            await store.Peers.GetOrCreateAsync(Workspace, $"p{i:D3}", new JsonObject { ["k"] = values[i]?.DeepClone() }, null, Ct);
        }

        await store.Peers.GetOrCreateAsync(Workspace, "missing", new JsonObject { ["other"] = 1 }, null, Ct);
        await store.Peers.GetOrCreateAsync(
            Workspace,
            "keys",
            JsonNode.Parse("""{"a.b":1,"q\"x":1,"k[0]":1,"o'b":1,"%_":1,"$":1,"":1," ":1,"a ":2,"😀":1,"\u0000":1,"a":{"b":6},"arr":[1]}""")!.AsObject(),
            null,
            Ct);

        var messages = Strings.Select((content, i) =>
            new NewMessage("alice", content, 1, new JsonObject { ["i"] = i }, null)).ToList();
        await store.Messages.AppendAsync(Workspace, Session, messages, null, Ct);
    }
}
