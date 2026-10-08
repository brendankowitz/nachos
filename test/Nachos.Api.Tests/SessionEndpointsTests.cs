using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class SessionEndpointsTests : ApiTest
{
    [Fact]
    public async Task Create_WithScopes_IsExplicitlyDeferredWithoutMutation()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        var error = await Post(W + "/sessions", """{"id":"s","scopes":["scope"]}""", 501);
        error.GetProperty("detail").GetString().ShouldBe("Not implemented in this Nachos version");
        Ids(await Post(W + "/sessions/list")).ShouldBeEmpty();
        await Post(W + "/sessions", """{"id":"s","scopes":[]}""");
        Ids(await Post(W + "/sessions/list")).ShouldBe(["s"]);
    }

    [Theory]
    [InlineData("""{"id":"s","scopes":1}""", "scopes")]
    [InlineData("""{"id":"s","peers":{"p":null}}""", "peers", "p")]
    public async Task Create_InvalidMembershipShape_IsLocated(string body, params string[] path)
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        Location(await Post(W + "/sessions", body, 422), ["body", .. path.Cast<object>()]);
    }

    [Fact]
    public async Task DeletePeers_NullItem_IsLocated()
    {
        await SeedSession();
        Location(await Send(HttpMethod.Delete, S + "/peers", "[null]", 422), "body", 0);
    }

    [Fact]
    public async Task Create_WithPeers_ListsMembers()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        await Post(W + "/sessions", """{"id":"s","peers":{"p":{},"q":{"observe_me":false}}}""");
        Ids(await Send(HttpMethod.Get, S + "/peers")).ShouldBe(["p", "q"]);
        Ids(await Post(W + "/sessions/list")).ShouldBe(["s"]);
        var updated = await Send(HttpMethod.Put, S, """{"metadata":{"tag":1}}""");
        updated.GetProperty("is_active").GetBoolean().ShouldBeTrue();
        updated.GetProperty("metadata").GetProperty("tag").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task SetPeers_ReplacesMembership()
    {
        await SeedSession();
        await Post(S + "/peers", """{"p":{},"q":{}}""");
        await Send(HttpMethod.Put, S + "/peers", """{"r":{}}""");
        Ids(await Send(HttpMethod.Get, S + "/peers")).ShouldBe(["r"]);
    }

    [Fact]
    public async Task DeletePeers_BodyArray()
    {
        await SeedSession();
        await Post(S + "/peers", """{"p":{},"q":{}}""");
        await Send(HttpMethod.Delete, S + "/peers", """["p","missing"]""");
        Ids(await Send(HttpMethod.Get, S + "/peers")).ShouldBe(["q"]);
        Location(await Send(HttpMethod.Delete, S + "/peers", """["q",7]""", 422), "body", 1);
    }

    [Fact]
    public async Task PeerConfig_GetPut_204()
    {
        await SeedSession();
        await Post(S + "/peers", """{"p":{}}""");
        var config = await Send(HttpMethod.Get, S + "/peers/p/config");
        config.GetProperty("observe_me").ValueKind.ShouldBe(JsonValueKind.Null);
        config.GetProperty("observe_others").ValueKind.ShouldBe(JsonValueKind.Null);
        await Send(HttpMethod.Put, S + "/peers/p/config", """{"observe_me":false,"observe_others":true}""", 204);
        config = await Send(HttpMethod.Get, S + "/peers/p/config");
        config.GetProperty("observe_me").GetBoolean().ShouldBeFalse();
        config.GetProperty("observe_others").GetBoolean().ShouldBeTrue();
    }
}
