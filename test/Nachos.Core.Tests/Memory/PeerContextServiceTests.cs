using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Memory;
using Shouldly;

namespace Nachos.Core.Tests.Memory;

/// <summary>Peer card, representation and peer context through <see cref="NachosService"/>.</summary>
public sealed class PeerContextServiceTests
{
    private const string W = MemoryFixture.W;

    [Fact]
    public async Task CardAndRepresentationForTarget()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("alice likes tea", observer: "bob", observed: "alice");
        await f.AddAsync("bob likes coffee", observer: "bob", observed: "bob");
        await f.Service.SetPeerCardAsync(W, "bob", ["IDENTITY: alice"], target: "alice");
        await f.Service.SetPeerCardAsync(W, "bob", ["IDENTITY: bob"]);

        var aboutAlice = await f.Service.GetPeerContextAsync(W, "bob", new(Target: "alice"));
        var aboutHimself = await f.Service.GetPeerContextAsync(W, "bob", new());

        aboutAlice.ShouldSatisfyAllConditions(
            c => c.PeerId.ShouldBe("bob"),
            c => c.TargetId.ShouldBe("alice"),
            c => c.Representation.ShouldBe("## Explicit\n\n- alice likes tea\n"),
            c => c.PeerCard.ShouldBe(["IDENTITY: alice"]));
        aboutHimself.ShouldSatisfyAllConditions(
            c => c.TargetId.ShouldBe("bob"),
            c => c.Representation.ShouldBe("## Explicit\n\n- bob likes coffee\n"),
            c => c.PeerCard.ShouldBe(["IDENTITY: bob"]));
    }

    [Fact]
    public async Task Context_WithoutCardOrConclusions_IsEmpty()
    {
        using var f = await new MemoryFixture().SeedAsync();

        var context = await f.Service.GetPeerContextAsync(W, "alice", new(Target: "bob"));

        context.ShouldBe(new PeerContext("alice", "bob", "", null));
    }

    [Fact]
    public async Task PutCard_ReplacesTheEntries_AsSent()
    {
        using var f = await new MemoryFixture().SeedAsync();
        (await f.Service.GetPeerCardAsync(W, "bob", "alice")).PeerCard.ShouldBeNull();

        await f.Service.SetPeerCardAsync(W, "bob", ["first"], "alice");
        // No cap and no category prefix rule on PUT (owner decision Q4).
        var entries = Enumerable.Range(0, 60).Select(i => $"free text {i}").ToArray();
        (await f.Service.SetPeerCardAsync(W, "bob", entries, "alice")).PeerCard.ShouldBe(entries);

        (await f.Service.GetPeerCardAsync(W, "bob", "alice")).PeerCard.ShouldBe(entries);
        (await f.Service.GetPeerCardAsync(W, "bob")).PeerCard.ShouldBeNull();
        (await f.Service.SetPeerCardAsync(W, "bob", [], "alice")).PeerCard.ShouldBeEmpty();
    }

    [Fact]
    public async Task PutCard_RejectsMissingPeersAndNullEntries()
    {
        using var f = await new MemoryFixture().SeedAsync();

        await Should.ThrowAsync<NotFoundException>(() => f.Service.SetPeerCardAsync(W, "bob", ["x"], "nobody"));
        var error = (await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.SetPeerCardAsync(W, "bob", ["x", null!], "alice"))).Errors.ShouldHaveSingleItem();
        error.Loc.ShouldBe(["body", "peer_card", 1]);
        await Should.ThrowAsync<RequestValidationException>(() => f.Service.SetPeerCardAsync(W, "bob", null!, "alice"));
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.GetPeerCardAsync(W, "bob", "bad target"));
        await Should.ThrowAsync<NotFoundException>(() => f.Service.GetPeerCardAsync("missing", "bob"));
    }

    [Fact]
    public async Task Representation_UsesTheRequestOptions()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("alice likes tea");
        await f.AddAsync("alice in S2", session: "S2");

        var all = await f.Service.GetRepresentationAsync(W, "bob", new(Target: "alice"));
        var s2 = await f.Service.GetRepresentationAsync(W, "bob", new(Target: "alice", SessionId: "S2"));
        var filtered = await f.Service.GetRepresentationAsync(W, "bob",
            new(Target: "alice", Filters: new JsonObject { ["session_id"] = "S1" }));
        var capped = await f.Service.GetRepresentationAsync(W, "bob", new(Target: "alice", MaxConclusions: 1));

        all.Representation.ShouldBe("## Explicit\n\n- alice likes tea\n- alice in S2\n");
        s2.Representation.ShouldBe("## Explicit\n\n- alice in S2\n");
        filtered.Representation.ShouldBe("## Explicit\n\n- alice likes tea\n");
        capped.Representation.ShouldBe("## Explicit\n\n- alice in S2\n");
    }

    [Theory]
    [InlineData("max_conclusions", 0, "greater_than_equal")]
    [InlineData("max_conclusions", 101, "less_than_equal")]
    [InlineData("search_top_k", 0, "greater_than_equal")]
    [InlineData("search_top_k", 101, "less_than_equal")]
    [InlineData("search_max_distance", -0.5, "greater_than_equal")]
    [InlineData("search_max_distance", 1.5, "less_than_equal")]
    public async Task Bounds_Are422_InTheBodyOrTheQuery(string field, double value, string type)
    {
        using var f = await new MemoryFixture().SeedAsync();
        var count = (int)value;
        PeerRepresentationGet body = field switch
        {
            "max_conclusions" => new(MaxConclusions: count),
            "search_top_k" => new(SearchTopK: count),
            _ => new(SearchMaxDistance: value),
        };
        PeerContextQuery query = field switch
        {
            "max_conclusions" => new(MaxConclusions: count),
            "search_top_k" => new(SearchTopK: count),
            _ => new(SearchMaxDistance: value),
        };

        var bodyError = (await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.GetRepresentationAsync(W, "bob", body))).Errors.ShouldHaveSingleItem();
        var queryError = (await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.GetPeerContextAsync(W, "bob", query))).Errors.ShouldHaveSingleItem();

        bodyError.Loc.ShouldBe(["body", field]);
        bodyError.Type.ShouldBe(type);
        queryError.Loc.ShouldBe(["query", field]);
        queryError.Type.ShouldBe(type);
    }

    [Fact]
    public async Task Bounds_AcceptTheirLimits()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("alice likes tea");

        var text = await f.Service.GetRepresentationAsync(W, "bob", new(Target: "alice", MaxConclusions: 100,
            SearchQuery: "alice likes tea", SearchTopK: 100, SearchMaxDistance: 0, IncludeMostFrequent: true));
        await f.Service.GetPeerContextAsync(W, "bob", new(MaxConclusions: 1, SearchTopK: 1, SearchMaxDistance: 1));

        text.Representation.ShouldBe("## Explicit\n\n- alice likes tea\n");
    }

    [Theory]
    [InlineData("\"q1\"")]
    [InlineData("[\"q1\",\"q2\"]")]
    [InlineData("[]")]
    public async Task Scope_Is422_NotSupported(string scope)
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("alice likes tea");

        var error = await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.GetRepresentationAsync(W, "bob", new(Target: "alice", Scope: JsonNode.Parse(scope))));

        error.Detail.ShouldBe(VisibilityPolicy.ScopeNotSupported);
        error.Detail.ShouldBe("scope is not supported in this Nachos version");
    }
}
