using Nachos.Abstractions.Domain;
using Nachos.Core.Tests.Deriver;
using Nachos.Core.Memory;
using Shouldly;

namespace Nachos.Core.Tests.Memory;

public sealed class RepresentationBuilderTests
{
    private static Task<string> Build(MemoryFixture f, RepresentationQuery query, string observer = "bob", string observed = "alice") =>
        f.Representations.BuildAsync(MemoryFixture.W, observer, observed, VisibilityPolicy.Unrestricted, query, default);

    [Fact]
    public async Task MergesSourcesWithoutDuplicates()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("alice likes tea");
        var frequent = await f.AddAsync("alice lives in Oslo");
        await f.AddAsync("alice has a cat");
        await f.CorroborateAsync(frequent, times: 2);

        // "alice lives in Oslo" is the semantic match, the most frequent and one of the most recent at once.
        var text = await Build(f, new(IncludeMostFrequent: true, SearchQuery: "alice lives in Oslo", SearchTopK: 1));

        text.ShouldBe("""
            ## Explicit

            - alice lives in Oslo
            - alice likes tea
            - alice has a cat

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task DeterministicOrder()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("first");
        await f.AddAsync("deduced", session: null, level: ConclusionLevel.Deductive);
        await f.AddAsync("second");
        await f.AddAsync("third");

        var query = new RepresentationQuery(SearchQuery: "third", SearchTopK: 1);
        var text = await Build(f, query);

        // Level sections in level order; within one, rank (the semantic match before recent ones), then creation time.
        text.ShouldBe("""
            ## Explicit

            - third
            - first
            - second

            ## Deductive

            - deduced

            """.ReplaceLineEndings("\n"));
        (await Build(f, query)).ShouldBe(text);
    }

    [Fact]
    public async Task MaxConclusionsCaps()
    {
        using var f = await new MemoryFixture().SeedAsync();
        for (var i = 1; i <= 5; i++)
        {
            await f.AddAsync($"fact {i}");
        }

        var text = await Build(f, new(MaxConclusions: 3));

        // The newest three survive and render oldest first.
        text.ShouldBe("""
            ## Explicit

            - fact 3
            - fact 4
            - fact 5

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task MaxConclusionsCaps_KeepsTheBestRankedSourceFirst()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("old match");
        await f.AddAsync("newer");
        await f.AddAsync("newest");

        var text = await Build(f, new(MaxConclusions: 2, SearchQuery: "old match", SearchTopK: 1));

        text.ShouldBe("""
            ## Explicit

            - old match
            - newest

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task NoQuery_SkipsSemantic()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("alice likes tea");
        var calls = f.Embeddings.Calls;

        (await Build(f, new())).ShouldContain("- alice likes tea");
        f.Embeddings.Calls.ShouldBe(calls);

        // Without a query, no embedding generator is needed at all.
        var withoutAi = new RepresentationBuilder(f.Store, embeddings: null);
        (await withoutAi.BuildAsync(MemoryFixture.W, "bob", "alice", VisibilityPolicy.Unrestricted, new(), default))
            .ShouldContain("- alice likes tea");
        await Should.ThrowAsync<InvalidOperationException>(() =>
            withoutAi.BuildAsync(MemoryFixture.W, "bob", "alice", VisibilityPolicy.Unrestricted, new(SearchQuery: "tea"), default));
    }

    [Fact]
    public async Task SearchMaxDistance_LeavesOutFartherMatches()
    {
        using var f = await new MemoryFixture().SeedAsync();
        f.Embeddings.Vectors["near"] = FlakyEmbeddingGenerator.Axis(0);
        f.Embeddings.Vectors["far"] = FlakyEmbeddingGenerator.Axis(2);
        f.Embeddings.Vectors["query"] = FlakyEmbeddingGenerator.Axis(0, tilt: 0.1f);
        await f.AddAsync("far");
        await f.AddAsync("near");
        await f.AddAsync("recent filler");

        // MaxConclusions 1 leaves room only for the best-ranked source; "far" is beyond the distance and never ranks.
        var text = await Build(f, new(MaxConclusions: 1, SearchQuery: "query", SearchMaxDistance: 0.1));

        text.ShouldBe("## Explicit\n\n- near\n");
    }

    [Fact]
    public async Task OnlyTheObserversCollectionOfTheTarget_IsUsed()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.AddAsync("bob about alice");
        await f.AddAsync("alice about herself", observer: "alice");
        await f.AddAsync("bob about himself", observed: "bob");

        (await Build(f, new())).ShouldBe("## Explicit\n\n- bob about alice\n");
        (await Build(f, new(), observer: "alice")).ShouldBe("## Explicit\n\n- alice about herself\n");
    }

    [Fact]
    public async Task NoConclusions_IsEmpty()
    {
        using var f = await new MemoryFixture().SeedAsync();

        (await Build(f, new(IncludeMostFrequent: true, SearchQuery: "anything"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task MultilineContent_StaysOneBullet()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.Conclusions.CreateAsync(MemoryFixture.W, [new("line one\nline two", "bob", "alice", "S1")], default);

        (await Build(f, new())).ShouldBe("## Explicit\n\n- line one line two\n");
    }
}
