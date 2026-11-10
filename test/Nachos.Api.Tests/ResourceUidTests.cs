using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed partial class ResourceUidTests : ApiTest
{
    [Fact]
    public async Task Uid_IsStableAcrossGetOrCreateUpdateAndList()
    {
        await SeedSession();
        var workspace = Uid(await Post("/v3/workspaces", """{"id":"w"}"""));
        var peer = Uid(await Post(W + "/peers", """{"id":"p"}"""));
        var session = Uid(await Post(W + "/sessions", """{"id":"s"}"""));

        new[] { workspace, peer, session }.ShouldAllBe(uid => HexUid().IsMatch(uid));
        new[] { workspace, peer, session }.Distinct().Count().ShouldBe(3);
        Uid(await Send(HttpMethod.Put, W, """{"metadata":{"k":1}}""")).ShouldBe(workspace);
        Uid(await Send(HttpMethod.Put, W + "/peers/p", """{"metadata":{"k":1}}""")).ShouldBe(peer);
        Uid(await Send(HttpMethod.Put, S, """{"metadata":{"k":1}}""")).ShouldBe(session);
        Uid((await Post("/v3/workspaces/list")).GetProperty("items")[0]).ShouldBe(workspace);
        Uid((await Post(W + "/peers/list")).GetProperty("items")[0]).ShouldBe(peer);
        Uid((await Post(W + "/sessions/list")).GetProperty("items")[0]).ShouldBe(session);
    }

    [Fact]
    public async Task SameNameInAnotherWorkspace_HasAnotherUid()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        await Post("/v3/workspaces", """{"id":"v"}""");

        var first = Uid(await Post(W + "/peers", """{"id":"p"}"""));
        var second = Uid(await Post("/v3/workspaces/v/peers", """{"id":"p"}"""));

        second.ShouldNotBe(first);
    }

    private static string Uid(JsonElement resource) => resource.GetProperty("uid").GetString()!;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HexUid();
}
