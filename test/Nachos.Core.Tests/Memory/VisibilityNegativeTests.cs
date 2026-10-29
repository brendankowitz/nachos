using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Core.Memory;
using Shouldly;

namespace Nachos.Core.Tests.Memory;

/// <summary>
/// Review Focus 5: each M2 memory consumer honours <see cref="VisibilityPolicy"/>. An allowlist shows only explicit
/// conclusions of the allowlisted sessions and no peer card; an empty allowlist shows nothing.
/// </summary>
public sealed class VisibilityNegativeTests
{
    private const string W = MemoryFixture.W;

    private static readonly VisibilityPolicy OnlyS1 = VisibilityPolicy.Create(["S1"], null);
    private static readonly VisibilityPolicy Nothing = VisibilityPolicy.Create([], null);

    /// <summary>
    /// bob's memory of alice: one visible explicit S1 fact; an S2 fact, an unattributed fact, an unattributed deduction
    /// and a deduction attributed to S1 that are not visible under the S1 allowlist.
    /// </summary>
    private static async Task<MemoryFixture> SeededAsync()
    {
        var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("alice likes tea", session: "S1");
        await f.AddAsync("alice likes tea in S2", session: "S2");
        await f.AddAsync("alice likes tea, unattributed", session: null);
        await f.AddAsync("alice likes tea, deduced", session: null, level: ConclusionLevel.Deductive);
        await f.AddAsync("alice likes tea, deduced in S1", session: "S1", level: ConclusionLevel.Deductive);
        await f.PeerContext.SetCardAsync(W, "bob", "alice", ["IDENTITY: alice"], default);
        return f;
    }

    private static RepresentationQuery Everything => new(IncludeMostFrequent: true, SearchQuery: "alice likes tea", SearchTopK: 100);

    [Fact]
    public async Task Representation_AllowlistedSession_ExcludesOthers()
    {
        using var f = await SeededAsync();

        (await f.Representations.BuildAsync(W, "bob", "alice", OnlyS1, Everything, default))
            .ShouldBe("## Explicit\n\n- alice likes tea\n");
        (await f.Representations.BuildAsync(W, "bob", "alice", VisibilityPolicy.Unrestricted, Everything, default))
            .ShouldContain("deduced");
    }

    [Fact]
    public async Task Representation_EmptyAllowlist_FailsClosed()
    {
        using var f = await SeededAsync();

        (await f.Representations.BuildAsync(W, "bob", "alice", Nothing, Everything, default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Card_OmittedUnderAllowlist()
    {
        using var f = await SeededAsync();

        (await f.PeerContext.GetCardAsync(W, "bob", "alice", VisibilityPolicy.Unrestricted, default)).ShouldBe(["IDENTITY: alice"]);
        (await f.PeerContext.GetCardAsync(W, "bob", "alice", OnlyS1, default)).ShouldBeNull();
        (await f.PeerContext.GetCardAsync(W, "bob", "alice", Nothing, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Context_AllowlistedSession_ExcludesOthers_AndOmitsTheCard()
    {
        using var f = await SeededAsync();

        var context = await f.PeerContext.GetContextAsync(W, "bob", "alice", OnlyS1, Everything, default);
        var closed = await f.PeerContext.GetContextAsync(W, "bob", "alice", Nothing, Everything, default);

        context.Representation.ShouldBe("## Explicit\n\n- alice likes tea\n");
        context.PeerCard.ShouldBeNull();
        closed.Representation.ShouldBeEmpty();
        closed.PeerCard.ShouldBeNull();
    }

    [Fact]
    public async Task ConclusionList_AllowlistedSession_ExcludesOthers()
    {
        using var f = await SeededAsync();

        (await f.Conclusions.ListAsync(W, OnlyS1, null, new(), default)).Items.Select(c => c.Content).ShouldBe(["alice likes tea"]);
        (await f.Conclusions.ListAsync(W, Nothing, null, new(), default)).Items.ShouldBeEmpty();
        (await f.Conclusions.ListAsync(W, VisibilityPolicy.Unrestricted, null, new(), default)).Items.Count.ShouldBe(5);
    }

    [Fact]
    public async Task ConclusionList_UnderAllowlist_TotalAndPagesCountOnlyVisibleRows()
    {
        using var f = await SeededAsync();
        await f.AddAsync("alice likes coffee", session: "S1");

        var page = await f.Conclusions.ListAsync(W, OnlyS1, null, new(Size: 1), default);

        // The level rule is applied in the store, so the S1 deduction counts in neither the total nor the pages.
        page.Items.Select(c => c.Content).ShouldBe(["alice likes tea"]);
        page.Total.ShouldBe(2);
        page.Pages.ShouldBe(2);
        (await f.Conclusions.ListAsync(W, OnlyS1, null, new(Page: 2, Size: 1), default)).Items
            .Select(c => c.Content).ShouldBe(["alice likes coffee"]);
    }

    [Fact]
    public async Task Representation_UnderAllowlist_IsNotCrowdedOutByHiddenRows()
    {
        using var f = await SeededAsync();
        for (var i = 0; i < 3; i++)
        {
            await f.AddAsync($"newer deduction {i}", session: "S1", level: ConclusionLevel.Deductive);
        }

        // Three newer hidden rows would fill a one-conclusion page if the level were only checked after the read.
        (await f.Representations.BuildAsync(W, "bob", "alice", OnlyS1, new(MaxConclusions: 1), default))
            .ShouldBe("## Explicit\n\n- alice likes tea\n");
    }

    [Fact]
    public async Task Conclusion_OfAnotherWorkspace_IsNotFound_AndCannotBeDeleted()
    {
        using var f = await SeededAsync();
        await f.Service.GetOrCreateWorkspaceAsync("W2");
        var id = (await f.Conclusions.ListAsync(W, VisibilityPolicy.Unrestricted, null, new(), default)).Items[0].PublicId;

        (await f.Conclusions.GetAsync("W2", VisibilityPolicy.Unrestricted, id, default)).ShouldBeNull();
        await Should.ThrowAsync<NotFoundException>(() => f.Service.GetConclusionAsync("W2", id));
        await f.Service.DeleteConclusionAsync("W2", id);

        (await f.Service.GetConclusionAsync(W, id)).Id.ShouldBe(id);
        (await f.Service.ListConclusionsAsync("W2", null, new())).Total.ShouldBe(0);
    }

    [Fact]
    public async Task ConclusionQuery_AllowlistedSession_ExcludesOthers()
    {
        using var f = await SeededAsync();

        (await f.Conclusions.QueryAsync(W, OnlyS1, "alice likes tea", 100, null, null, default))
            .Select(c => c.Content).ShouldBe(["alice likes tea"]);
        (await f.Conclusions.QueryAsync(W, Nothing, "alice likes tea", 100, null, null, default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ConclusionGet_OutsideTheAllowlist_IsNotVisible()
    {
        using var f = await SeededAsync();
        var all = (await f.Conclusions.ListAsync(W, VisibilityPolicy.Unrestricted, null, new(), default)).Items;

        foreach (var conclusion in all)
        {
            var visible = await f.Conclusions.GetAsync(W, OnlyS1, conclusion.PublicId, default);
            (visible is not null).ShouldBe(conclusion.Content == "alice likes tea", conclusion.Content);
            (await f.Conclusions.GetAsync(W, Nothing, conclusion.PublicId, default)).ShouldBeNull();
        }
    }

    [Fact]
    public void Policy_ExcludesNonExplicitConclusions_EvenInAnAllowlistedSession()
    {
        var deduced = new ConclusionRecord("c1", W, "bob", "alice", "S1", ConclusionLevel.Deductive, "x", 1, [], new(), DateTimeOffset.UnixEpoch);

        OnlyS1.Allows(deduced).ShouldBeFalse();
        OnlyS1.Allows(deduced with { Level = ConclusionLevel.Explicit }).ShouldBeTrue();
        OnlyS1.Allows(deduced with { Level = ConclusionLevel.Explicit, SessionName = "S2" }).ShouldBeFalse();
        VisibilityPolicy.Unrestricted.Allows(deduced).ShouldBeTrue();
    }

    [Fact]
    public void Policy_AnyScope_FailsClosed()
    {
        Should.Throw<NachosValidationException>(() => VisibilityPolicy.Create(null, ["q1"]))
            .Detail.ShouldBe(VisibilityPolicy.ScopeNotSupported);
        Should.Throw<NachosValidationException>(() => VisibilityPolicy.Create(["S1"], []));
        VisibilityPolicy.Create(null, null).ShouldBeSameAs(VisibilityPolicy.Unrestricted);
        OnlyS1.IncludesPeerCard.ShouldBeFalse();
        VisibilityPolicy.Unrestricted.IncludesPeerCard.ShouldBeTrue();
    }
}
