using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Memory;
using Nachos.Core.Tests.Memory;
using Shouldly;
using static Nachos.Core.Tests.Context.ContextKit;

namespace Nachos.Core.Tests.Context;

/// <summary>The <c>GET S/context</c> query rules (spec §11.6): 422s carry the query parameter's location.</summary>
public sealed class SessionContextValidationTests
{
    private const string W = MemoryFixture.W;

    private static async Task<ValidationError> InvalidAsync(SessionContextQuery query)
    {
        using var f = await new MemoryFixture().SeedAsync();
        var thrown = await Should.ThrowAsync<RequestValidationException>(() => f.Service.GetSessionContextAsync(W, "S1", query));
        return thrown.Errors.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PerspectiveWithoutTarget_422() =>
        (await InvalidAsync(new SessionContextQuery(PeerPerspective: "bob"))).Loc.ShouldBe(["query", "peer_perspective"]);

    [Fact]
    public async Task ScopeWithPerspective_422() =>
        (await InvalidAsync(new SessionContextQuery(PeerTarget: "alice", PeerPerspective: "bob", Scope: "s")))
            .Loc.ShouldBe(["query", "scope"]);

    [Fact]
    public async Task ScopeWithoutTarget_422() =>
        (await InvalidAsync(new SessionContextQuery(Scope: "s"))).Loc.ShouldBe(["query", "scope"]);

    [Fact]
    public async Task SessionsWithLimitToSession_422() =>
        (await InvalidAsync(new SessionContextQuery(PeerTarget: "alice", Sessions: ["S1"], LimitToSession: true)))
            .ShouldSatisfyAllConditions(e => e.Loc.ShouldBe(["query", "sessions"]), e => e.Type.ShouldBe("value_error"));

    [Fact]
    public async Task SessionsWithScope_422() =>
        (await InvalidAsync(new SessionContextQuery(PeerTarget: "alice", Scope: "s", Sessions: ["S1"])))
            .ShouldSatisfyAllConditions(e => e.Loc.ShouldBe(["query", "sessions"]), e => e.Type.ShouldBe("value_error"));

    [Fact]
    public async Task SessionsWithoutThisSession_422() =>
        (await InvalidAsync(new SessionContextQuery(PeerTarget: "alice", Sessions: ["S2"])))
            .ShouldSatisfyAllConditions(e => e.Loc.ShouldBe(["query", "sessions"]), e => e.Type.ShouldBe("value_error"));

    [Fact]
    public async Task EmptySessions_WithLimitToSession_Accepted()
    {
        using var f = await new MemoryFixture().SeedAsync();

        (await f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(Sessions: [], LimitToSession: true))).Id
            .ShouldBe("S1");
    }

    [Fact]
    public async Task Scope_422NotSupported()
    {
        using var f = await new MemoryFixture().SeedAsync();

        var thrown = await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(PeerTarget: "alice", Scope: "s")));

        thrown.Detail.ShouldBe(VisibilityPolicy.ScopeNotSupported);
    }

    [Theory]
    [InlineData(0, "greater_than_equal")]
    [InlineData(-1, "greater_than_equal")]
    [InlineData(100_001, "less_than_equal")]
    public async Task TokensOutOfBounds_422(int tokens, string type)
    {
        var error = await InvalidAsync(new SessionContextQuery(Tokens: tokens));

        error.Loc.ShouldBe(["query", "tokens"]);
        error.Type.ShouldBe(type);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100_000)]
    public async Task TokensAtBounds_Accepted(int tokens)
    {
        using var f = await new MemoryFixture().SeedAsync();

        (await f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(Tokens: tokens))).Id.ShouldBe("S1");
    }

    [Fact]
    public async Task RepresentationBounds_SameAsPeerContext()
    {
        (await InvalidAsync(new SessionContextQuery(MaxConclusions: 0))).Loc.ShouldBe(["query", "max_conclusions"]);
        (await InvalidAsync(new SessionContextQuery(SearchTopK: 101))).Loc.ShouldBe(["query", "search_top_k"]);
        (await InvalidAsync(new SessionContextQuery(SearchMaxDistance: 1.5))).Loc.ShouldBe(["query", "search_max_distance"]);
    }

    [Fact]
    public async Task InvalidIds_422()
    {
        using var f = await new MemoryFixture().SeedAsync();

        await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(PeerTarget: "bad id!")));
        await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(PeerTarget: "alice", PeerPerspective: "bad id!")));
        await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(Sessions: ["S2", "bad id!"])));
    }

    [Fact]
    public async Task NoTokens_Uses100000()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await AppendAsync(f, "S1", [Words(1, "oldest"), Words(60_000, "older"), Words(40_000, "newest")]);

        var context = await f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery());

        context.Messages.Select(m => m.Content.Split(' ', 2)[0]).ShouldBe(["older", "newest"]);
    }
}
