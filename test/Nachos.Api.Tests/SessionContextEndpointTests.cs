using System.Text.Json;
using Nachos.Api.Tests.Auth;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class SessionContextEndpointTests : MemoryApiTest
{
    private const string Context = S + "/context";

    private async Task SeedMessages()
    {
        await SeedPeers();
        await Post(M, """{"messages":[{"content":"first","peer_id":"alice"},{"content":"second","peer_id":"bob"},{"content":"third","peer_id":"alice"}]}""", 201);
    }

    private static string[] Contents(JsonElement context) =>
        [.. context.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("content").GetString()!)];

    [Fact]
    public async Task Context_ReturnsTheMessagesInOrder_AndNoRepresentationWithoutATarget()
    {
        await SeedMessages();

        var context = await Send(HttpMethod.Get, Context);

        context.GetProperty("id").GetString().ShouldBe("s");
        Contents(context).ShouldBe(["first", "second", "third"]);
        context.GetProperty("summary").ValueKind.ShouldBe(JsonValueKind.Null);
        context.GetProperty("peer_representation").ValueKind.ShouldBe(JsonValueKind.Null);
        context.GetProperty("peer_card").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Context_TokenBudget_DropsWholeMessages()
    {
        await SeedMessages();

        // Each message is one token or more, so a one-token budget keeps at most the newest.
        Contents(await Send(HttpMethod.Get, Context + "?tokens=1")).ShouldBeSubsetOf(["third"]);
        Contents(await Send(HttpMethod.Get, Context + "?tokens=100000")).ShouldBe(["first", "second", "third"]);
    }

    [Fact]
    public async Task Context_WithATarget_IncludesTheRepresentation()
    {
        await SeedMessages();
        await Conclude("Alice likes tea", session: "s");

        var context = await Send(HttpMethod.Get, Context + "?peer_target=alice");

        context.GetProperty("peer_representation").GetString()!.ShouldContain("Alice likes tea");
    }

    [Fact]
    public async Task Context_RepeatedSessions_AreAllBound()
    {
        await SeedMessages();
        await Post(W + "/sessions", """{"id":"s2"}""");

        // Only the first value names this session, so binding the last value alone would be a 422.
        await Send(HttpMethod.Get, Context + "?peer_target=alice&sessions=s&sessions=s2");
        Location(await Send(HttpMethod.Get, Context + "?peer_target=alice&sessions=s2", status: 422), "query", "sessions");
    }

    [Theory]
    [InlineData("tokens=0", "tokens")]
    [InlineData("tokens=100001", "tokens")]
    [InlineData("tokens=many", "tokens")]
    [InlineData("summary=maybe", "summary")]
    [InlineData("peer_perspective=alice", "peer_perspective")]
    [InlineData("peer_target=alice&sessions=s&limit_to_session=true", "sessions")]
    [InlineData("scope=named", "scope")]
    [InlineData("peer_target=alice&search_top_k=0", "search_top_k")]
    [InlineData("peer_target=alice&max_conclusions=101", "max_conclusions")]
    public async Task Context_InvalidParameters_AreLocated422s(string query, string parameter)
    {
        await SeedMessages();

        Location(await Send(HttpMethod.Get, Context + "?" + query, status: 422), "query", parameter);
    }

    [Fact]
    public async Task Context_ScopeWithATarget_Is422()
    {
        await SeedMessages();

        // The older detail-only form, kept until M6 (spec §9.4 entry 16).
        Problem(await Send(HttpMethod.Get, Context + "?peer_target=alice&scope=named", status: 422), 422, JsonValueKind.String);
    }

    [Fact]
    public async Task Context_MissingSession_Is404()
    {
        await SeedMessages();

        await Send(HttpMethod.Get, W + "/sessions/missing/context", status: 404);
    }

    [Fact]
    public async Task Summaries_AreNullUntilMade()
    {
        await SeedMessages();

        var summaries = await Send(HttpMethod.Get, S + "/summaries");

        summaries.GetProperty("id").GetString().ShouldBe("s");
        summaries.GetProperty("short_summary").ValueKind.ShouldBe(JsonValueKind.Null);
        summaries.GetProperty("long_summary").ValueKind.ShouldBe(JsonValueKind.Null);
        await Send(HttpMethod.Get, W + "/sessions/missing/summaries", status: 404);
    }

    [Fact]
    public async Task OpenApi_DocumentsSessionsAsARepeatedStringParameter()
    {
        var operation = (await Send(HttpMethod.Get, "/openapi/v1.json")).GetProperty("paths")
            .GetProperty("/v3/workspaces/{workspace_id}/sessions/{session_id}/context").GetProperty("get");
        var sessions = operation.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "sessions").GetProperty("schema");
        sessions.GetProperty("items").GetProperty("type").GetString().ShouldBe("string");
        var tokens = operation.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "tokens").GetProperty("schema");
        tokens.GetProperty("maximum").GetInt32().ShouldBe(100_000);
    }

    // The S/context gate for scoped keys (plan Task 9, carried from the Task 6 review). Seed: p1 is a member of s1 and s2,
    // p2 alone of s3. Honcho's OpenAPI: "A peer-scoped key must be an active member of every session named"; its platform
    // reference answers 401 for a named non-member session on the chat endpoint, and every Nachos auth failure is a 401.

    private static async Task<AuthHost> ScopedHost()
    {
        var host = new AuthHost();
        await host.Seed();
        var admin = AuthHost.Key("admin");
        await host.Send("POST", "/v3/workspaces/A/sessions", """{"id":"s2","peers":{"p1":{}}}""", admin, 200);
        await host.Send("POST", "/v3/workspaces/A/sessions", """{"id":"s3","peers":{"p2":{}}}""", admin, 200);
        return host;
    }

    private const string ScopedContext = "/v3/workspaces/A/sessions/s1/context";

    [Fact]
    public async Task Context_PeerKey_MemberOfEveryNamedSession_200()
    {
        using var host = await ScopedHost();

        await host.Send("GET", ScopedContext + "?peer_target=p1&sessions=s1&sessions=s2", null, AuthHost.Key("peer"), 200);
        await host.Send("GET", ScopedContext + "?peer_target=p1&limit_to_session=true", null, AuthHost.Key("peer"), 200);
    }

    [Fact]
    public async Task Context_PeerKey_NonMemberSessionsParam_401()
    {
        using var host = await ScopedHost();
        var peer = AuthHost.Key("peer");

        await host.Send("GET", ScopedContext + "?peer_target=p1&sessions=s1&sessions=s3", null, peer, 401);
        await host.Send("GET", ScopedContext + "?peer_target=p1&sessions=s1&sessions=missing", null, peer, 401);
        await host.Store.Sessions.RemovePeersAsync("A", "s2", ["p1"], default);
        await host.Send("GET", ScopedContext + "?peer_target=p1&sessions=s1&sessions=s2", null, peer, 401);
        // A workspace key passes the allowlist as given.
        await host.Send("GET", ScopedContext + "?peer_target=p1&sessions=s1&sessions=s3", null, AuthHost.Key("workspace"), 200);
    }

    [Fact]
    public async Task Context_PeerKey_NonMemberSession_401()
    {
        using var host = await ScopedHost();

        await host.Send("GET", "/v3/workspaces/A/sessions/s3/context", null, AuthHost.Key("peer"), 401);
        await host.Send("GET", "/v3/workspaces/A/sessions/s3/summaries", null, AuthHost.Key("peer"), 401);
        await host.Send("POST", "/v3/workspaces/A/sessions/s3/search", """{"query":"q"}""", AuthHost.Key("peer"), 401);
    }

    [Fact]
    public async Task Context_PeerKey_OtherPerspective_401()
    {
        using var host = await ScopedHost();
        var peer = AuthHost.Key("peer");

        await host.Send("GET", ScopedContext + "?peer_target=p2", null, peer, 401);
        await host.Send("GET", ScopedContext + "?peer_target=p1&peer_perspective=p2", null, peer, 401);
        // Its own view of another peer is its own perspective.
        await host.Send("GET", ScopedContext + "?peer_target=p2&peer_perspective=p1", null, peer, 200);
        await host.Send("GET", ScopedContext + "?peer_target=p2", null, AuthHost.Key("workspace"), 200);
    }

    [Fact]
    public async Task Context_ScopedKeyNamingAScope_401()
    {
        using var host = await ScopedHost();

        // Honcho's OpenAPI: scope "requires a workspace- or admin-level key". A workspace key reaches the 422 instead.
        await host.Send("GET", ScopedContext + "?peer_target=p1&scope=named", null, AuthHost.Key("peer"), 401);
        await host.Send("GET", ScopedContext + "?peer_target=p1&scope=named", null, AuthHost.Key("session"), 401);
        await host.Send("GET", ScopedContext + "?peer_target=p1&scope=named", null, AuthHost.Key("workspace"), 422);
    }

    [Fact]
    public async Task Context_SessionKey_NamesOnlyItsOwnSession()
    {
        using var host = await ScopedHost();
        var session = AuthHost.Key("session");

        // Honcho's platform reference: "A session-scoped key is confined to its own session".
        await host.Send("GET", ScopedContext + "?peer_target=p1&sessions=s1", null, session, 200);
        await host.Send("GET", ScopedContext + "?peer_target=p1&sessions=s1&sessions=s2", null, session, 401);
    }

    [Fact]
    public async Task Context_SessionKey_PerspectiveOnlyWhenNarrowedToItsSession()
    {
        using var host = await ScopedHost();
        var session = AuthHost.Key("session");
        await host.Send("PUT", "/v3/workspaces/A/peers/p1/card", """{"peer_card":["p1 likes tea"]}""", AuthHost.Key("admin"), 200);

        // Unnarrowed, a perspective is a peer's global view and card; the docs keep session keys off peer routes.
        await host.Send("GET", ScopedContext + "?peer_target=p1", null, session, 401);
        await host.Send("GET", ScopedContext + "?peer_perspective=p1", null, session, 401);
        await host.Send("GET", ScopedContext, null, session, 200);
        foreach (var narrowed in new[] { "?peer_target=p1&limit_to_session=true", "?sessions=s1&peer_target=p1" })
        {
            var context = await host.Send("GET", ScopedContext + narrowed, null, session, 200);
            context.GetProperty("peer_card").ValueKind.ShouldBe(JsonValueKind.Null, narrowed);
        }
        // The same request under a workspace key does get the card, so its absence above is the narrowing.
        (await host.Send("GET", ScopedContext + "?peer_target=p1", null, AuthHost.Key("workspace"), 200))
            .GetProperty("peer_card").GetArrayLength().ShouldBe(1);
    }

    // The handler and the route read the query through one binder (SessionContextEndpoints.ReadQuery). Each variant would
    // pass authorization with one reading and act on another if the two sides ever parsed differently.
    [Theory]
    [InlineData("peer_target=p2&peer_target=p1&limit_to_session=true", 200, 200)]
    [InlineData("peer_target=p1&peer_target=p2&limit_to_session=true", 401, 200)]
    [InlineData("Peer_Target=p2", 401, 401)]
    [InlineData("peer_target=", 401, 401)]
    [InlineData("peer_target=p1&sessions=s1,s2", 401, 401)]
    [InlineData("peer_target=p1&%73essions=s1&%73essions=s3", 401, 401)]
    [InlineData("peer_target=p1&%73essions=s1", 200, 200)]
    [InlineData("peer_target=p1&LIMIT_TO_SESSION=true", 200, 200)]
    [InlineData("peer_target=p1&scope=", 401, 401)]
    [InlineData("peer_perspective=p1", 422, 401)]
    public async Task Context_HandlerAndRoute_ReadTheSameParameters(string query, int peerStatus, int sessionStatus)
    {
        using var host = await ScopedHost();

        await host.Send("GET", ScopedContext + "?" + query, null, AuthHost.Key("peer"), peerStatus);
        await host.Send("GET", ScopedContext + "?" + query, null, AuthHost.Key("session"), sessionStatus);
    }

    [Fact]
    public async Task Context_MoreThan100Sessions_IsALocated422_BeforeAnyMembershipQuery()
    {
        using var host = await ScopedHost();
        // None of these sessions has p1 as a member, so a membership query before the cap would make the peer key's
        // answer a 401; the 422 shows the cap comes first.
        var many = string.Join('&', Enumerable.Range(0, 101).Select(i => "sessions=s" + i));
        var hundred = "sessions=s1&" + string.Join('&', Enumerable.Range(0, 99).Select(i => "sessions=x" + i));

        foreach (var identity in new[] { "peer", "workspace" })
        {
            var body = await host.Send("GET", ScopedContext + "?peer_target=p1&" + many, null, AuthHost.Key(identity), 422);
            body.GetProperty("detail")[0].GetProperty("loc").EnumerateArray().Select(value => value.GetString())
                .ShouldBe(["query", "sessions"], identity);
        }
        // Exactly 100 is within the cap: the peer key then fails on membership, the workspace key passes.
        await host.Send("GET", ScopedContext + "?peer_target=p1&" + hundred, null, AuthHost.Key("peer"), 401);
        await host.Send("GET", ScopedContext + "?peer_target=p1&" + hundred, null, AuthHost.Key("workspace"), 200);
    }
}
