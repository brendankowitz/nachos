using System.Globalization;
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
    private const string Forty = "1234567890123456789012345678901234567890";

    private static readonly string Nines1000 = new('9', 1000);
    private static readonly string TenToMinus1000 = "0." + new string('0', 999) + "1";

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

        // Beyond decimal and double, up to 1000 digits (indexes 18 to 24, peers p043 to p049).
        Nines1000, "-" + Nines1000, TenToMinus1000, "1e999", Forty, "1.00", "0." + Nines1000,

        // Exponent spellings, stored as written (indexes 25 to 32, peers p050 to p057).
        "1E400", "-1e-400", "5E-324", "0e5", "1e999999999999999999999", "79228162514264337593543950335", "1.5E+3",
        "-1e999999999999999999999",
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
        Nines1000, "-" + Nines1000, "1" + Nines1000, "1e999", "1e1000", "-1e1000", "1e400000", "-1e400000", TenToMinus1000,
        "1e-1000", "1e-1001", "1e-400000", "-1e-400000", "0." + Nines1000, "0." + Nines1000 + "9", Forty, Forty + ".0", "4.0e1",
        "1E400", "-1e-400", "-0.1e-399", "5E-324", "4.9E-324", "0.5e-323", "0e5", "-0.0", "1E+2", "100e0",
        "1e999999999999999999999", "10e999999999999999999998", "-1e999999999999999999999", "0.01e1000000000000000000",
        "1e999999999999999998", "1.5e3", "1500.00", "79228162514264337593543950335",
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
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":1}}"""))).ShouldBe(["p026", "p027", "p048"], ignoreOrder: true);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":100.0}}"""))).ShouldBe(["p031", "p032"], ignoreOrder: true);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":0,"lt":1e-28}}}"""))).ShouldBe(["p036", "p037", "p045", "p052"], ignoreOrder: true);

        // Large, tiny and exponent-spelled values compare exactly, stored text unchanged.
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":1e999}}}"""))).ShouldBe(["p043", "p054"], ignoreOrder: true);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"lt":-1e999}}}"""))).ShouldBe(["p044", "p057"], ignoreOrder: true);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":1e-1000}}"""))).ShouldBe(["p045"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":0,"lt":1e-999}}}"""))).ShouldBe(["p045"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":1e1000}}}"""))).ShouldBe(["p054"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":0,"lt":1e-1001}}}"""))).ShouldBeEmpty();
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":1.234567890123456789012345678901234567890e39}}"""))).ShouldBe(["p047"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":{"gt":0.99,"lt":1}}}"""))).ShouldBe(["p049"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":1E400}}"""))).ShouldBe(["p050"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":-0.1e-399}}"""))).ShouldBe(["p051"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":0.5e-323}}"""))).ShouldBe(["p052"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":-0.0}}"""))).ShouldBe(["p025", "p041", "p053"], ignoreOrder: true);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":10e999999999999999999998}}"""))).ShouldBe(["p054"]);
        (await ListAsync(store, ResourceKind.Peer, Parse("""{"metadata":{"k":1500.00}}"""))).ShouldBe(["p056"]);

        // An operand longer than a LIKE pattern may be still matches exactly.
        (await ListAsync(store, ResourceKind.Message, Parse(new JsonObject { ["content"] = new JsonObject { ["contains"] = new string('x', 4100) + "need" } }.ToJsonString())))
            .ShouldBe(["23"]);

        static FilterNode? Parse(string json) => FilterParser.Parse(json, json.Contains("content", StringComparison.Ordinal) ? ResourceKind.Message : ResourceKind.Peer);
    }

    // ------------------------------------------------------------------------------------------------ lists and merges

    /// <summary>
    /// <c>in</c> lists, array containment and merged same-path conditions (review round 2): multi-number lists with
    /// negative and long order keys, the 100/101-character short-key boundary, lists spanning many packed chunks, NOT over
    /// a merged AND, merged and mixed-kind ranges, nested paths, and string lists across the packed-length boundary.
    /// Seeded in a workspace of their own, so the hand-numbered sentinels above are unaffected.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListFilters))]
    public async Task ListsAndMergedConditions_MatchTheReference(string filterJson)
    {
        var store = await SeedListsAsync();
        var filter = FilterParser.Parse(filterJson, ResourceKind.Peer);

        var expected = await ListAsync(Reference, ResourceKind.Peer, filter, ListWorkspace);
        var actual = await ListAsync(store, ResourceKind.Peer, filter, ListWorkspace);

        actual.ShouldBe(expected, ignoreOrder: true, filterJson.Length > 300 ? filterJson[..300] : filterJson);
    }

    private const string ListWorkspace = "differential-lists";
    private static bool _listsSeeded;

    /// <summary>
    /// <c>1.33…3</c> + <paramref name="last"/> with <paramref name="digits"/> significant digits. Its order key has
    /// <c>13 + digits</c> characters (one more when negative), so 87 and 88 digits straddle the 100-character short-key limit.
    /// </summary>
    private static string Mantissa(int digits, char last = '7') => "1." + new string('3', digits - 2) + last;

    private static List<string> ListNumbers()
    {
        var list = new List<string> { "0", "-0", "1", "-1", "1.2", "1.23", "12", "123", "-1.2", "-1.23", "-12", "11.2", "0.5", "-0.5", "3", "4", "5", "-5", "1e2", "-1e-2", "2.5", "7" };
        for (var digits = 82; digits <= 92; digits++)
        {
            list.AddRange([Mantissa(digits), "-" + Mantissa(digits), Mantissa(digits, '8'), "-" + Mantissa(digits, '8')]);
        }

        list.AddRange([new string('9', 500), "-" + new string('9', 500)]);

        // Order keys of 7996 to 8001 characters (13 + digits positive, 14 + digits negative) and 9000-digit numbers.
        for (var digits = 7982; digits <= 7988; digits++)
        {
            list.AddRange([Mantissa(digits), "-" + Mantissa(digits)]);
        }

        list.AddRange([Mantissa(9000), "-" + Mantissa(9000)]);
        return list;
    }

    /// <summary>
    /// Strings around the raw/digest limit (16 UTF-16 units, with surrogate pairs straddling it), around 1990 to 2010
    /// units, and with characters the packing must not confuse.
    /// </summary>
    private static readonly string[] ListStrings =
    [
        "", " ", "a", "a ", "A", "|", "x|y", "||", "😀", "\uFFFF", "\u0000", "1", "-0", new string('s', 1998) + "t",
        new string('s', 1999), new string('s', 2000), new string('s', 1999) + " ", new string('s', 4100),
        new string('p', 15), new string('p', 16), new string('p', 17), new string('p', 15) + " ", new string('p', 16) + " ",
        new string('p', 14) + "😀", new string('p', 15) + "😀", new string('p', 13) + "😀|", "\u0000" + new string('p', 16),
        new string('p', 16) + "\u0000", new string('q', 1990), new string('q', 2001), new string('q', 2010), new string('q', 1995) + "😀",
    ];

    private static readonly string[] ListOtherValues =
    [
        "\"a\"", "true", "false", "null", "{\"x\":1}", "[]", "[1,2,3]", "[-1,1.0,\"a\",true]", "[" + Mantissa(88) + ",-5]",
        "[\"1\",1]", "[[1]]", "[3,4,5,-5]",
    ];

    public static TheoryData<string> ListFilters()
    {
        var data = new TheoryData<string>();
        static string L(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";
        static string Q(string s) => JsonValue.Create(s)!.ToJsonString();
        var numbers = ListNumbers();

        // Number lists: the whole numeric set with other kinds, negative and long keys, nested paths.
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L([.. numbers.Take(30), "\"a\"", "true"]) + "}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L(numbers.Skip(20)) + "}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":[1.23,11.2,-1.23,\"1\",false]}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":[1.2,-12]}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":[" + Mantissa(87) + "," + Mantissa(88) + ",-" + Mantissa(86) + ",-" + Mantissa(87) + "]}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":[" + Mantissa(87, '8') + "," + Mantissa(88, '8') + ",-" + Mantissa(86, '8') + ",-" + Mantissa(87, '8') + ",0]}}}");
        data.Add("{\"metadata\":{\"o\":{\"x\":{\"in\":[" + Mantissa(89) + ",-" + Mantissa(90) + ",5]}}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":[1,null,\"a\"]}}}");

        // Lists spanning many chunks: 980 fillers (89-character keys in the short tier, 104-character in the long tier)
        // with real values spread across them.
        string[] reals = ["1", "-1", "1.23", "-1.2", "0", Mantissa(88), "-" + Mantissa(87), "5", "-5", "123", Mantissa(90, '8'), "-" + Mantissa(91)];
        foreach (var fillerDigits in new[] { 70, 85 })
        {
            var list = Enumerable.Range(0, 980).Select(j => "1." + new string('2', fillerDigits) + j.ToString("D4", CultureInfo.InvariantCulture) + "1").ToList();
            for (var r = 0; r < reals.Length; r++)
            {
                list.Insert(r * 97 % list.Count, reals[r]);
            }

            data.Add("{\"metadata\":{\"k\":{\"in\":" + L(list) + "}}}");
            data.Add("{\"NOT\":[{\"metadata\":{\"k\":{\"in\":" + L(list) + "}}}]}");
        }

        // Merged same-path conditions, their negation, and the same under a nested path.
        foreach (var range in new[] { "{\"gt\":1,\"lt\":5}", "{\"gte\":-5,\"lte\":1.2}", "{\"gt\":\"\",\"lt\":5}", "{\"gt\":0,\"ne\":3}", "{\"gt\":-1,\"in\":[1,2,3,4,\"a\"]}", "{\"gte\":1,\"lt\":1.3}", "{\"contains\":\"a\",\"gt\":0}", "{\"gt\":-1e400,\"lt\":1e400}" })
        {
            data.Add("{\"metadata\":{\"k\":" + range + "}}");
            data.Add("{\"NOT\":[{\"metadata\":{\"k\":" + range + "}}]}");
            data.Add("{\"metadata\":{\"o\":{\"x\":" + range + "}}}");
        }

        data.Add("{\"AND\":[{\"metadata\":{\"k\":[1]}},{\"metadata\":{\"k\":{\"contains\":\"a\"}}}]}");
        data.Add("{\"AND\":[{\"metadata\":{\"k\":[3,-5]}},{\"metadata\":{\"k\":\"*\"}}]}");
        data.Add("{\"AND\":[{\"metadata\":{\"k\":{\"gt\":1}}},{\"metadata\":{\"k\":{\"lt\":5}}},{\"metadata\":{\"k\":null}}]}");
        data.Add("{\"OR\":[{\"metadata\":{\"k\":{\"gt\":1,\"lt\":3}}},{\"metadata\":{\"k\":{\"gt\":4,\"lt\":6}}}]}");
        data.Add("{\"NOT\":[{\"metadata\":{\"k\":{\"gt\":1,\"lt\":3}}},{\"metadata\":{\"k\":{\"gt\":4,\"lt\":6}}}]}");

        // Array containment.
        data.Add("{\"metadata\":{\"k\":[1,-1,\"a\",true]}}");
        data.Add("{\"metadata\":{\"k\":[1.0,2,3]}}");
        data.Add("{\"metadata\":{\"k\":[" + Mantissa(88) + ",-5.0]}}");
        data.Add("{\"metadata\":{\"k\":[3,4,5,-5,3e0]}}");
        data.Add("{\"metadata\":{\"k\":[\"1\"]}}");
        data.Add("{\"NOT\":[{\"metadata\":{\"k\":[1]}}]}");

        // String lists: exact code units and trailing spaces, the packed-length boundary, mixed with numbers.
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L(ListStrings.Select(Q)) + "}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L(ListStrings.Where((_, i) => i % 2 == 0).Select(Q)) + "}}}");
        data.Add("{\"NOT\":[{\"metadata\":{\"k\":{\"in\":" + L(ListStrings.Where((_, i) => i % 2 == 1).Select(Q)) + "}}}]}");
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L([Q(new string('s', 1999)), Q(new string('s', 2000)), Q("a"), "1", "-0"]) + "}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L([Q(new string('s', 1999) + " "), Q(new string('s', 4100)), Q("|"), "true"]) + "}}}");
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L([Q("x"), Q("y|"), Q("a  "), Q("😀"), Q("\uFFFF"), Q("\u0000"), Q("A")]) + "}}}");
        data.Add("{\"metadata\":{\"o\":{\"x\":{\"in\":" + L([.. Enumerable.Range(0, 997).Select(i => Q("f" + i.ToString(CultureInfo.InvariantCulture))), Q("a "), Q("||"), "1"]) + "}}}}");

        // Raw/digest boundary: near misses of the 15 to 17-unit strings and the stored ones, by kind and nested.
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L([Q(new string('p', 16)), Q(new string('p', 17) + " "), Q(new string('p', 14) + "😀"), Q(new string('p', 14) + "😁"), Q(new string('p', 13) + "😀|"), Q("\u0000" + new string('p', 16))]) + "}}}");
        data.Add("{\"NOT\":[{\"metadata\":{\"k\":{\"in\":" + L([Q(new string('p', 15)), Q(new string('p', 17)), Q(new string('p', 15) + "😀"), Q(new string('q', 2001))]) + "}}}]}");
        data.Add("{\"metadata\":{\"o\":{\"x\":{\"in\":" + L([Q(new string('p', 16) + " "), Q(new string('p', 16) + "\u0000"), Q(new string('q', 2010)), Q(new string('q', 1995) + "😀"), Q(new string('q', 1995) + "😁")]) + "}}}}");

        // Digest chunks: 990 long strings and 990 long numbers (every chunk range gated) with the stored values spread
        // across them, so matches fall in first, middle and last chunks.
        var longStrings = Enumerable.Range(0, 990).Select(i => Q(new string('q', 1990) + i.ToString("D4", CultureInfo.InvariantCulture))).ToList();
        string[] storedLong = [Q(new string('q', 1990)), Q(new string('q', 2001)), Q(new string('q', 2010)), Q(new string('p', 17)), Q(new string('s', 4100)), Q(new string('p', 15) + "😀")];
        for (var r = 0; r < storedLong.Length; r++)
        {
            longStrings.Insert(r * 173 % longStrings.Count, storedLong[r]);
        }

        data.Add("{\"metadata\":{\"k\":{\"in\":" + L(longStrings) + "}}}");
        data.Add("{\"NOT\":[{\"metadata\":{\"o\":{\"x\":{\"in\":" + L(longStrings) + "}}}}]}");
        var longNumbers = Enumerable.Range(0, 990).Select(i => "1." + new string('4', 120) + i.ToString("D4", CultureInfo.InvariantCulture) + "1").ToList();
        string[] storedNumbers = [Mantissa(88), "-" + Mantissa(89, '8'), Mantissa(7985), "-" + Mantissa(7987), Mantissa(9000), new string('9', 500), "1", "-1.23"];
        for (var r = 0; r < storedNumbers.Length; r++)
        {
            longNumbers.Insert(r * 131 % longNumbers.Count, storedNumbers[r]);
        }

        data.Add("{\"metadata\":{\"k\":{\"in\":" + L(longNumbers) + "}}}");
        data.Add("{\"NOT\":[{\"metadata\":{\"o\":{\"x\":{\"in\":" + L(longNumbers) + "}}}}]}");

        // Long numbers: the stored values with near misses (one digit off, sign flipped, another length).
        data.Add("{\"metadata\":{\"k\":{\"in\":" + L([Mantissa(7983), Mantissa(7984, '8'), "-" + Mantissa(7986), "-" + Mantissa(7988, '8'), Mantissa(9000, '8'), "-" + Mantissa(9000), Mantissa(7989)]) + "}}}");
        data.Add("{\"metadata\":{\"o\":{\"x\":{\"in\":" + L([Mantissa(7982), "-" + Mantissa(7982), Mantissa(9000), Mantissa(8999)]) + "}}}}");
        data.Add("{\"metadata\":{\"k\":{\"gt\":" + Mantissa(7985) + ",\"lt\":" + Mantissa(9000) + "}}}");
        return data;
    }

    private async Task<SqlMemoryStore> SeedListsAsync()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "filter-differential");
        var store = database.CreateStore(TimeProvider.System);
        await SeedLock.WaitAsync(Ct);
        try
        {
            if (!_listsSeeded)
            {
                await SeedListsAsync(Reference);
                await SeedListsAsync(store);
                _listsSeeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }

        return store;
    }

    private static async Task SeedListsAsync(IMemoryStore store)
    {
        await store.Workspaces.GetOrCreateAsync(ListWorkspace, null, null, Ct);
        var i = 0;
        async Task PeerAsync(JsonObject metadata) => await store.Peers.GetOrCreateAsync(ListWorkspace, $"l{i++:D4}", metadata, null, Ct);
        foreach (var value in ListNumbers().Select(n => JsonNode.Parse(n)).Concat(ListStrings.Select(s => (JsonNode?)JsonValue.Create(s))))
        {
            await PeerAsync(new JsonObject { ["k"] = value?.DeepClone(), ["o"] = new JsonObject { ["x"] = value?.DeepClone() } });
        }

        foreach (var value in ListOtherValues)
        {
            await PeerAsync(new JsonObject { ["k"] = JsonNode.Parse(value) });
        }

        await PeerAsync(new JsonObject { ["other"] = 1 });
    }

    private static async Task<List<string>> ListAsync(IMemoryStore store, ResourceKind kind, FilterNode? filter, string workspace = Workspace)
    {
        var ids = new List<string>();
        if (kind == ResourceKind.Peer)
        {
            await foreach (var peer in ((Func<PageRequest, CancellationToken, Task<Page<PeerRecord>>>)((page, ct) =>
                store.Peers.ListAsync(workspace, PeerKind.All, filter, page, ct))).EnumerateAsync(100))
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
