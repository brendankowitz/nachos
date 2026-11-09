using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.Core.Memory;
using Nachos.Core.Search;
using Nachos.Core.Tests.Deriver;
using Nachos.Core.Tests.Memory;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests.Search;

/// <summary>
/// Review Focus 3: search returns only what the caller may see. Each rule is checked against the real in-memory store and
/// against a store that returns everything it is asked about, so Core's own check is shown to hold on its own.
/// </summary>
public sealed class SearchVisibilityNegativeTests
{
    private const string W = MemoryFixture.W;

    private static async Task<MemoryFixture> TeaInBothSessionsAsync()
    {
        var f = await new MemoryFixture().SeedAsync();
        f.Embeddings.Vectors["tea"] = FlakyEmbeddingGenerator.Axis(0);
        await f.PostAsync("alice", "tea in S1", "S1", FlakyEmbeddingGenerator.Axis(0));
        await f.PostAsync("alice", "tea in S2", "S2", FlakyEmbeddingGenerator.Axis(0));
        return f;
    }

    private static MessageRecord Message(string session, string content) =>
        new($"m-{session}-{content}", W, session, "alice", 1, content, 1, new JsonObject(), DateTimeOffset.UnixEpoch);

    /// <summary>A store whose search returns <paramref name="hits"/> for every request, whatever its scope or filter.</summary>
    private static IMemoryStore LyingStore(params MessageRecord[] hits)
    {
        var search = Substitute.For<IMessageSearch>();
        IReadOnlyList<MessageHit> ranked = [.. hits.Select((m, i) => new MessageHit(m, i + 1))];
        search.LexicalAsync(default!, default!, default, default).ReturnsForAnyArgs(ranked);
        search.VectorAsync(default!, default, default, default).ReturnsForAnyArgs(ranked);
        var store = Substitute.For<IMemoryStore>();
        store.Search.Returns(search);
        return store;
    }

    private static NachosService ServiceOver(IMemoryStore store)
    {
        var counter = new TiktokenTokenCounter();
        return NachosServiceFactory.Create(store, counter, new RequestValidator(Options.Create(new NachosOptions()), counter),
            Substitute.For<IKeyIssuer>());
    }

    [Fact]
    public async Task SessionSearch_NeverReturnsOtherSession()
    {
        using var f = await TeaInBothSessionsAsync();

        var s1 = await f.Service.SearchSessionAsync(W, "S1", new MessageSearchOptions("tea"));
        var lied = await ServiceOver(LyingStore(Message("S2", "tea in S2"), Message("S1", "tea in S1")))
            .SearchSessionAsync(W, "S1", new MessageSearchOptions("tea"));

        s1.Select(m => m.Content).ShouldBe(["tea in S1"]);
        lied.Select(m => m.Content).ShouldBe(["tea in S1"]);
    }

    [Fact]
    public async Task PeerSearch_OutsideWindow_Excluded()
    {
        using var f = await new MemoryFixture().SeedAsync();
        f.Embeddings.Vectors["tea"] = FlakyEmbeddingGenerator.Axis(0);
        await f.PostAsync("alice", "tea before carol joined", vector: FlakyEmbeddingGenerator.Axis(0));
        await f.Store.Sessions.AddPeersAsync(W, "S1", new Dictionary<string, SessionPeerConfig> { ["carol"] = new() }, default);
        await f.PostAsync("alice", "tea while carol is a member", vector: FlakyEmbeddingGenerator.Axis(0));
        await f.Store.Sessions.RemovePeersAsync(W, "S1", ["carol"], default);
        await f.PostAsync("alice", "tea after carol left", vector: FlakyEmbeddingGenerator.Axis(0));
        await f.PostAsync("alice", "tea in a session carol never joined", "S2", FlakyEmbeddingGenerator.Axis(0));

        var carol = await f.Service.SearchPeerAsync(W, "carol", new MessageSearchOptions("tea"));
        var nobody = await f.Service.SearchPeerAsync(W, "nobody", new MessageSearchOptions("tea"));

        carol.Select(m => m.Content).ShouldBe(["tea while carol is a member"]);
        nobody.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptyAllowlist_FailsClosed()
    {
        using var f = await TeaInBothSessionsAsync();
        var nothing = VisibilityPolicy.Create([], null);
        var everywhere = new MessageSearchRequest(W, new MessageSearchScope.Workspace(), null);
        var lying = LyingStore(Message("S1", "tea in S1"));

        (await f.Search.SearchAsync(everywhere, "tea", 10, nothing, default)).ShouldBeEmpty();
        (await new HybridSearchService(lying).SearchAsync(everywhere, "tea", 10, nothing, default)).ShouldBeEmpty();
        await lying.Search.Received().LexicalAsync(
            Arg.Is<MessageSearchRequest>(r => r.Filter is FilterNode.MatchNone), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Allowlist_NarrowsWorkspaceSearch()
    {
        using var f = await TeaInBothSessionsAsync();
        var onlyS1 = VisibilityPolicy.Create(["S1"], null);
        var everywhere = new MessageSearchRequest(W, new MessageSearchScope.Workspace(), null);

        var found = await f.Search.SearchAsync(everywhere, "tea", 10, onlyS1, default);
        var lied = await new HybridSearchService(LyingStore(Message("S2", "tea in S2"), Message("S1", "tea in S1")))
            .SearchAsync(everywhere, "tea", 10, onlyS1, default);

        found.Select(m => m.Content).ShouldBe(["tea in S1"]);
        lied.Select(m => m.Content).ShouldBe(["tea in S1"]);
        (await f.Search.SearchAsync(everywhere, "tea", 10, VisibilityPolicy.Unrestricted, default)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task AllowlistFilter_IsAndedWithTheCallersFilter()
    {
        using var f = await TeaInBothSessionsAsync();
        await f.PostAsync("bob", "tea from bob in S1", "S1", FlakyEmbeddingGenerator.Axis(0));
        var byAlice = new FilterNode.Field(FilterColumns.PeerId, FilterOp.Eq, JsonValue.Create("alice"));
        var request = new MessageSearchRequest(W, new MessageSearchScope.Workspace(), byAlice);

        var found = await f.Search.SearchAsync(request, "tea", 10, VisibilityPolicy.Create(["S1"], null), default);

        found.Select(m => m.Content).ShouldBe(["tea in S1"]);
    }
}
