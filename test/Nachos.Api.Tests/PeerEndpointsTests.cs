using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions.Stores;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class PeerEndpointsTests : ApiTest
{
    [Fact]
    public async Task List_KindScope_ExcludesRegular()
    {
        await SeedSession();
        await Post(W + "/peers", """{"id":"regular"}""");
        await Factory.Services.GetRequiredService<IMemoryStore>().Peers.GetOrCreateAsync(
            "w", "scope", null, null, CancellationToken.None, isInternal: true);
        Ids(await Post(W + "/peers/list", """{"kind":"scope"}""")).ShouldBe(["scope"]);
        Ids(await Post(W + "/peers/list")).ShouldBe(["regular"]);
        Ids(await Post(W + "/peers/list", """{"kind":"all"}""")).ShouldBe(["regular", "scope"]);
        Location(await Post(W + "/peers/list", """{"kind":"regular"}""", 422), "body", "kind");
    }

    [Fact]
    public async Task Sessions_ForPeer()
    {
        await SeedSession();
        await Post(S + "/peers", """{"p":{}}""");
        Ids(await Post(W + "/peers/p/sessions")).ShouldBe(["s"]);
        await Send(HttpMethod.Delete, S + "/peers", """["p"]""");
        Ids(await Post(W + "/peers/p/sessions")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Update_ReplacesMetadataAndConfiguration()
    {
        await SeedSession();
        await Post(W + "/peers", """{"id":"p","metadata":{"old":1}}""");
        var peer = await Send(HttpMethod.Put, W + "/peers/p",
            """{"metadata":{"new":2},"configuration":{"observe_me":false}}""");
        peer.GetProperty("metadata").GetRawText().ShouldBe("""{"new":2}""");
        peer.GetProperty("configuration").GetProperty("observe_me").GetBoolean().ShouldBeFalse();
    }
}
