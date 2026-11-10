using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class SearchEndpointTests : MemoryApiTest
{
    private const string Other = W + "/sessions/other";

    private async Task SeedMessages()
    {
        await SeedPeers();
        await Post(M, """{"messages":[{"content":"I like green tea","peer_id":"alice"},{"content":"The train was late","peer_id":"bob"}]}""", 201);
        await Post(W + "/sessions", """{"id":"other"}""");
        await Post(Other + "/messages", """{"messages":[{"content":"green tea in the other session","peer_id":"alice"}]}""", 201);
    }

    private static string[] Contents(JsonElement found) =>
        [.. found.EnumerateArray().Select(message => message.GetProperty("content").GetString()!)];

    private static string[] Sessions(JsonElement found) =>
        [.. found.EnumerateArray().Select(message => message.GetProperty("session_id").GetString()!).Distinct()];

    [Fact]
    public async Task WorkspaceSearch_FindsMatchesInEverySession()
    {
        await SeedMessages();

        var found = await Post(W + "/search", """{"query":"green tea"}""");

        found.ValueKind.ShouldBe(JsonValueKind.Array);
        Contents(found).ShouldContain("I like green tea");
        Contents(found).ShouldContain("green tea in the other session");
        found[0].GetProperty("workspace_id").GetString().ShouldBe("w");
    }

    [Fact]
    public async Task WorkspaceSearch_HonoursLimitAndFilters()
    {
        await SeedMessages();

        (await Post(W + "/search", """{"query":"green tea","limit":1}""")).GetArrayLength().ShouldBe(1);
        var bobs = await Post(W + "/search", """{"query":"green tea train","filters":{"peer_id":"bob"}}""");
        Contents(bobs).ShouldBe(["The train was late"]);
    }

    [Fact]
    public async Task SessionSearch_NeverReturnsAnotherSessionsMessage()
    {
        await SeedMessages();

        var found = await Post(S + "/search", """{"query":"green tea"}""");

        Contents(found).ShouldContain("I like green tea");
        Sessions(found).ShouldBe(["s"]);
        (await Post(W + "/sessions/missing/search", """{"query":"green tea"}""")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task SessionSearch_FilterCannotEscapeTheRouteSession()
    {
        await SeedMessages();

        var found = await Post(S + "/search",
            """{"query":"green tea","filters":{"OR":[{"session_id":"other"},{"peer_id":"x"}]}}""");
        var own = await Post(S + "/search",
            """{"query":"green tea","filters":{"OR":[{"session_id":"other"},{"peer_id":"alice"}]}}""");

        // The other session's "green tea" message matches the first branch, but the route's session still bounds the search.
        Contents(found).ShouldBeEmpty();
        Contents(own).ShouldBe(["I like green tea"]);
    }

    [Fact]
    public async Task PeerSearch_SeesOnlyThePeersSessions()
    {
        await SeedMessages();

        // bob is a member of s only (he joined by sending there), so the other session's match is outside his view.
        var found = await Post(W + "/peers/bob/search", """{"query":"green tea"}""");

        Contents(found).ShouldContain("I like green tea");
        Sessions(found).ShouldBe(["s"]);
        Sessions(await Post(Alice + "/search", """{"query":"green tea"}""")).ShouldBe(["s", "other"], ignoreOrder: true);
    }

    [Fact]
    public async Task BlankQuery_FindsNothing()
    {
        await SeedMessages();

        (await Post(W + "/search", """{"query":"  "}""")).GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("/search")]
    [InlineData("/sessions/s/search")]
    [InlineData("/peers/alice/search")]
    public async Task Validation_IsLocatedInTheBody(string route)
    {
        await SeedMessages();

        Location(await Post(W + route, "{}", 422), "body", "query");
        Location(await Post(W + route, """{"query":null}""", 422), "body", "query");
        Location(await Post(W + route, """{"query":"tea","limit":0}""", 422), "body", "limit");
        Location(await Post(W + route, """{"query":"tea","limit":101}""", 422), "body", "limit");
        Location(await Post(W + route, "[]", 422), "body");
        (await Post(W + route, """{"query":"tea","limit":100}""")).ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task WorkspaceSearch_Scope_Is422()
    {
        await SeedMessages();

        // The older detail-only form, kept until M6 (spec §9.4 entry 16).
        Problem(await Post(W + "/search", """{"query":"tea","scope":"named"}""", 422), 422, JsonValueKind.String);
    }

    [Fact]
    public async Task MissingWorkspace_Is404()
    {
        await Post("/v3/workspaces/missing/search", """{"query":"tea"}""", 404);
    }
}
