using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class PeerMemoryEndpointTests : MemoryApiTest
{
    [Fact]
    public async Task Card_IsNullUntilSet_ThenReplacedPerTarget()
    {
        await SeedPeers();
        (await Send(HttpMethod.Get, Alice + "/card")).GetProperty("peer_card").ValueKind.ShouldBe(JsonValueKind.Null);

        var set = await Send(HttpMethod.Put, Alice + "/card", """{"peer_card":["IDENTITY: Alice","likes tea"]}""");
        set.GetProperty("peer_card").EnumerateArray().Select(e => e.GetString()).ShouldBe(["IDENTITY: Alice", "likes tea"]);
        await Send(HttpMethod.Put, Alice + "/card?target=bob", """{"peer_card":["Bob is a friend"]}""");
        await Send(HttpMethod.Put, Alice + "/card", """{"peer_card":["replaced"]}""");

        (await Send(HttpMethod.Get, Alice + "/card")).GetProperty("peer_card").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["replaced"]);
        (await Send(HttpMethod.Get, Alice + "/card?target=bob")).GetProperty("peer_card")[0].GetString().ShouldBe("Bob is a friend");
    }

    [Theory]
    [InlineData("{}", new object[] { "body", "peer_card" })]
    [InlineData("""{"peer_card":null}""", new object[] { "body", "peer_card" })]
    [InlineData("""{"peer_card":["a",null]}""", new object[] { "body", "peer_card", 1 })]
    [InlineData("""{"peer_card":"a"}""", new object[] { "body", "peer_card" })]
    public async Task CardSet_InvalidBody_Is422(string body, object[] location)
    {
        await SeedPeers();
        Location(await Send(HttpMethod.Put, Alice + "/card", body, 422), location);
    }

    [Fact]
    public async Task CardSet_UnknownTarget_Is404()
    {
        await SeedPeers();
        await Send(HttpMethod.Put, Alice + "/card?target=nobody", """{"peer_card":["x"]}""", 404);
    }

    [Fact]
    public async Task Representation_ListsTheTargetsConclusions_AndAcceptsAnEmptyBody()
    {
        await SeedPeers();
        await Conclude("Alice likes tea");
        await Conclude("Bob owns a bicycle", observed: "bob");

        var own = (await Post(Alice + "/representation", "")).GetProperty("representation").GetString()!;
        own.ShouldContain("Alice likes tea");
        own.ShouldNotContain("bicycle");
        var ofBob = (await Post(Alice + "/representation", """{"target":"bob","search_query":"bicycle","include_most_frequent":true}"""))
            .GetProperty("representation").GetString()!;
        ofBob.ShouldContain("Bob owns a bicycle");
    }

    [Fact]
    public async Task Representation_SessionId_KeepsOnlyThatSessionsExplicitConclusions()
    {
        await SeedPeers();
        await Conclude("said in s", session: "s");
        await Conclude("said nowhere");

        var text = (await Post(Alice + "/representation", """{"session_id":"s"}""")).GetProperty("representation").GetString()!;
        text.ShouldContain("said in s");
        text.ShouldNotContain("said nowhere");
    }

    [Theory]
    [InlineData("max_conclusions", "0")]
    [InlineData("max_conclusions", "101")]
    [InlineData("search_top_k", "0")]
    [InlineData("search_top_k", "101")]
    [InlineData("search_max_distance", "-0.5")]
    [InlineData("search_max_distance", "1.5")]
    public async Task Representation_Bounds_Are422(string field, string value)
    {
        await SeedPeers();
        Location(await Post(Alice + "/representation", $$"""{"{{field}}":{{value}}}""", 422), "body", field);
    }

    [Theory]
    [InlineData("\"named\"")]
    [InlineData("[\"a\",\"b\"]")]
    public async Task Representation_AnyScope_FailsClosed(string scope)
    {
        await SeedPeers();
        var body = await Post(Alice + "/representation", $$"""{"scope":{{scope}}}""", 422);
        Problem(body, 422, JsonValueKind.String);
        body.GetProperty("detail").GetString().ShouldBe("scope is not supported in this Nachos version");
    }

    [Fact]
    public async Task Context_HoldsRepresentationAndCardForTheTarget()
    {
        await SeedPeers();
        await Conclude("Bob owns a bicycle", observed: "bob");
        await Send(HttpMethod.Put, Alice + "/card?target=bob", """{"peer_card":["Bob is a friend"]}""");

        var context = await Send(HttpMethod.Get, Alice + "/context?target=bob&max_conclusions=5&include_most_frequent=true&search_query=bike&search_top_k=3&search_max_distance=1");
        context.GetProperty("peer_id").GetString().ShouldBe("alice");
        context.GetProperty("target_id").GetString().ShouldBe("bob");
        context.GetProperty("representation").GetString()!.ShouldContain("Bob owns a bicycle");
        context.GetProperty("peer_card")[0].GetString().ShouldBe("Bob is a friend");

        var self = await Send(HttpMethod.Get, Alice + "/context");
        self.GetProperty("target_id").GetString().ShouldBe("alice");
        self.GetProperty("peer_card").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Context_OpenApi_DocumentsTheQueryBounds()
    {
        var operation = (await Send(HttpMethod.Get, "/openapi/v1.json")).GetProperty("paths")
            .GetProperty("/v3/workspaces/{workspace_id}/peers/{peer_id}/context").GetProperty("get");
        var query = operation.GetProperty("parameters").EnumerateArray()
            .Where(p => p.GetProperty("in").GetString() == "query")
            .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("schema"));
        foreach (var (name, minimum, maximum) in new[] { ("search_top_k", 1m, 100m), ("max_conclusions", 1m, 100m), ("search_max_distance", 0m, 1m) })
        {
            query[name].GetProperty("minimum").GetDecimal().ShouldBe(minimum, name);
            query[name].GetProperty("maximum").GetDecimal().ShouldBe(maximum, name);
        }
        query["target"].TryGetProperty("minimum", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("max_conclusions=0", "max_conclusions", "greater_than_equal")]
    [InlineData("max_conclusions=101", "max_conclusions", "less_than_equal")]
    [InlineData("max_conclusions=many", "max_conclusions", "int_parsing")]
    [InlineData("search_top_k=0", "search_top_k", "greater_than_equal")]
    [InlineData("search_max_distance=2", "search_max_distance", "less_than_equal")]
    [InlineData("search_max_distance=far", "search_max_distance", "float_parsing")]
    [InlineData("include_most_frequent=maybe", "include_most_frequent", "bool_parsing")]
    public async Task Context_QueryBounds_Are422AtTheQueryParameter(string query, string field, string type)
    {
        await SeedPeers();
        var body = await Send(HttpMethod.Get, Alice + "/context?" + query, status: 422);
        Location(body, "query", field);
        body.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe(type);
    }
}
