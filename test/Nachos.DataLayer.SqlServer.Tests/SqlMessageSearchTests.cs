using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory;
using Nachos.DataLayer.SqlServer.Search;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// SQL message search against the in-memory provider (the executable reference), on a server with Full-Text Search
/// (<see cref="SqlMessageSearchFtsTests"/>) and without it (<see cref="SqlMessageSearchNoFtsTests"/>). Each corpus is
/// seeded once per fixture into a database of its own, and only then is the full-text index maintenance run, so the index's
/// first population covers the whole corpus; the tests only read it.
/// </summary>
public abstract class SqlMessageSearchTests
{
    private const string Workspace = "ws-search";
    private const string Session = "sess-main";
    private const string Other = "sess-other";

    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    private static readonly ConcurrentDictionary<(Type Fixture, string Seed), Lazy<Task<Corpus>>> Corpora = new();

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Whether the fixture's server has Full-Text Search, so lexical search can reach <c>CONTAINSTABLE</c>.</summary>
    protected abstract bool HasFullText { get; }

    private protected abstract Task<SqlTestDatabase> CreateDatabaseAsync();

    [Fact]
    public async Task VectorRanking_MatchesInMemory()
    {
        var corpus = await CorpusAsync(SeedVectorsAsync);

        foreach (var (query, scope) in new (float[], MessageSearchScope)[]
        {
            (Vector(0), new MessageSearchScope.Workspace()),
            (Vector(1, 0, 0.3f), new MessageSearchScope.Workspace()),
            (Vector(0, 2, 1f), new MessageSearchScope.Session(Session)),
            (Vector(2), new MessageSearchScope.PeerPerspective("alice")),
        })
        {
            var request = new MessageSearchRequest(Workspace, scope, null);
            var expected = Describe(await corpus.Reference.Search.VectorAsync(request, query, 100, Ct));
            expected.ShouldNotBeEmpty();
            Describe(await corpus.Ready.Search.VectorAsync(request, query, 100, Ct)).ShouldBe(expected);
            Describe(await corpus.Ready.Search.VectorAsync(request, query, 2, Ct)).ShouldBe(expected.Take(2));
        }
    }

    [Fact]
    public async Task FullText_IsSubsetOfLike_AndEqualOnWordsAndPrefixes()
    {
        var corpus = await CorpusAsync(SeedWordsAsync);
        corpus.IsFullTextReady.ShouldBe(HasFullText);
        var request = new MessageSearchRequest(Workspace, new MessageSearchScope.Workspace(), null);

        // Terms that occur only at the start of words: both modes find the same messages.
        foreach (var terms in new[] { ["apple"], ["lemon", "lime"], ["LIME"], ["orange", "lemon"], ["kiwi"], ["melon"], new[] { "lemon" } })
        {
            var expected = Describe(await corpus.Reference.Search.LexicalAsync(request, terms, 100, Ct));

            // The fallback ranks exactly as the reference does; full-text ranks by RANK, so only its set must agree.
            Describe(await corpus.Like.Search.LexicalAsync(request, terms, 100, Ct)).ShouldBe(expected);
            Unranked(await corpus.Ready.Search.LexicalAsync(request, terms, 100, Ct)).ShouldBe(Unranked(expected), ignoreOrder: true);
        }

        // A prefix finds the longer word in both modes.
        foreach (var store in new[] { corpus.Like, corpus.Ready })
        {
            (await store.Search.LexicalAsync(request, ["lemon"], 100, Ct)).Select(hit => hit.Message.Content).ShouldContain("lemonade stand");
        }

        // Inside a word ("ade" in "lemonade") only the substring fallback matches.
        var inside = Unranked(await corpus.Like.Search.LexicalAsync(request, ["ade"], 100, Ct));
        inside.ShouldNotBeEmpty();
        Unranked(await corpus.Ready.Search.LexicalAsync(request, ["ade"], 100, Ct)).ShouldBeSubsetOf(inside);
    }

    [Fact]
    public async Task LongTerms_DoNotFail()
    {
        var corpus = await CorpusAsync(SeedWordsAsync);
        var request = new MessageSearchRequest(Workspace, new MessageSearchScope.Workspace(), null);
        var longest = IMessageSearch.MaxTermLength;

        // One longest word fits a full-text condition; five do not (over 4,000 characters) and fall back to LIKE, as does a
        // longest term of escaped wildcards (a 2,002-character pattern).
        foreach (var terms in new[]
        {
            [new string('a', longest)],
            [.. "abcde".Select(c => new string(c, longest))],
            new[] { new string('%', longest) },
        })
        {
            var expected = Describe(await corpus.Reference.Search.LexicalAsync(request, terms, 100, Ct));
            Describe(await corpus.Like.Search.LexicalAsync(request, terms, 100, Ct)).ShouldBe(expected);
            Describe(await corpus.Ready.Search.LexicalAsync(request, terms, 100, Ct)).ShouldBe(expected);
        }

        foreach (var store in new[] { corpus.Like, corpus.Ready })
        {
            await Should.ThrowAsync<ArgumentException>(
                () => store.Search.LexicalAsync(request, [new string('a', 4001)], 100, Ct));
        }
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("*")]
    [InlineData("lemon*")]
    [InlineData("NEAR")]
    [InlineData("AND")]
    [InlineData("(")]
    [InlineData("~")]
    [InlineData("the", "and")]
    [InlineData("東京")]
    [InlineData("a\"b", "\"\"")]
    [InlineData("x') OR 1=1 --")]
    [InlineData("lemon", "NEAR", "lime")]
    [InlineData("%", "_", "[", "\\")]
    public async Task HostileQueries_Return200(params string[] terms)
    {
        var corpus = await CorpusAsync(SeedWordsAsync);
        var request = new MessageSearchRequest(Workspace, new MessageSearchScope.Workspace(), null);
        var expected = Describe(await corpus.Reference.Search.LexicalAsync(request, terms, 100, Ct));

        Describe(await corpus.Like.Search.LexicalAsync(request, terms, 100, Ct)).ShouldBe(expected);

        // Full-text search matches words and word prefixes, so it can find fewer messages, never others. The index has no
        // stoplist, so a query of stopwords finds what the substring search finds.
        var ready = Unranked(await corpus.Ready.Search.LexicalAsync(request, terms, 100, Ct));
        if (terms.SequenceEqual(["the", "and"]))
        {
            expected.ShouldNotBeEmpty();
            ready.ShouldBe(Unranked(expected), ignoreOrder: true);
        }
        else
        {
            ready.ShouldBeSubsetOf(Unranked(expected));
        }
    }

    [Fact]
    public async Task LikeWildcards_AreLiteral()
    {
        var corpus = await CorpusAsync(SeedWordsAsync);
        var request = new MessageSearchRequest(Workspace, new MessageSearchScope.Workspace(), null);

        foreach (var (term, matches) in new (string, string[])[]
        {
            ("100%", ["100% sure"]),
            ("a_b", ["a_b literal"]),
            ("[x]", ["[x] marks"]),
            ("\\d", ["c:\\dir path"]),
            ("%", ["100% sure"]),
            ("_", ["a_b literal"]),
        })
        {
            Describe(await corpus.Reference.Search.LexicalAsync(request, [term], 100, Ct)).Select(Content).ShouldBe(matches);
            Describe(await corpus.Like.Search.LexicalAsync(request, [term], 100, Ct)).Select(Content).ShouldBe(matches);
            Describe(await corpus.Ready.Search.LexicalAsync(request, [term], 100, Ct)).Select(Content).ShouldBe(matches);
        }
    }

    [Fact]
    public async Task PeerPerspectiveWindows_MatchInMemory()
    {
        var corpus = await CorpusAsync(SeedVectorsAsync);

        foreach (var peer in new[] { "alice", "bob", "carol", "nobody" })
        {
            var request = new MessageSearchRequest(Workspace, new MessageSearchScope.PeerPerspective(peer), null);
            var lexical = Describe(await corpus.Reference.Search.LexicalAsync(request, ["zebra"], 100, Ct));
            Describe(await corpus.Like.Search.LexicalAsync(request, ["zebra"], 100, Ct)).ShouldBe(lexical);
            Unranked(await corpus.Ready.Search.LexicalAsync(request, ["zebra"], 100, Ct)).ShouldBe(Unranked(lexical), ignoreOrder: true);

            var vector = Describe(await corpus.Reference.Search.VectorAsync(request, Vector(0), 100, Ct));
            Describe(await corpus.Ready.Search.VectorAsync(request, Vector(0), 100, Ct)).ShouldBe(vector);
        }

        (await corpus.Reference.Search.LexicalAsync(
            new MessageSearchRequest(Workspace, new MessageSearchScope.PeerPerspective("alice"), null), ["zebra"], 100, Ct))
            .Count.ShouldBeGreaterThan(1);
    }

    /// <summary>A hit as the tests compare it: rank, content, session, sender and seq (public ids differ between stores).</summary>
    private static List<string> Describe(IReadOnlyList<MessageHit> hits) =>
        [.. hits.Select(hit => $"{hit.Rank}|{hit.Message.Content}|{hit.Message.SessionName}|{hit.Message.PeerName}|{hit.Message.Seq}")];

    private static string Content(string described) => described.Split('|')[1];

    /// <summary>The hits without their ranks, for comparing full-text results, which rank by <c>RANK</c>, as a set.</summary>
    private static List<string> Unranked(IReadOnlyList<MessageHit> hits) => Unranked(Describe(hits));

    private static List<string> Unranked(List<string> described) => [.. described.Select(hit => hit[(hit.IndexOf('|', StringComparison.Ordinal) + 1)..])];

    private static float[] Vector(int axis, int? towards = null, float tilt = 0)
    {
        var vector = new float[VectorSpace.Dimensions];
        vector[axis] = 1;
        if (towards is { } other)
        {
            vector[other] = tilt;
        }

        return vector;
    }

    private static NewMessage Msg(string peer, string content, DateTimeOffset createdAt) => new(peer, content, 1, null, createdAt);

    /// <summary>
    /// Seeds the same corpus into the in-memory reference and a new SQL database, then runs the full-text index
    /// maintenance until the index is ready (on a server without Full-Text Search, one check that must leave it not ready).
    /// </summary>
    private Task<Corpus> CorpusAsync(Func<IMemoryStore, FakeTimeProvider, Task> seed) =>
        Corpora.GetOrAdd((GetType(), seed.Method.Name), _ => new Lazy<Task<Corpus>>(() => SeedCorpusAsync(seed))).Value;

    private async Task<Corpus> SeedCorpusAsync(Func<IMemoryStore, FakeTimeProvider, Task> seed)
    {
        var referenceClock = new FakeTimeProvider(Start);
        var reference = new InMemoryMemoryStore(referenceClock);
        await seed(reference, referenceClock);

        var database = await CreateDatabaseAsync();
        var sqlClock = new FakeTimeProvider(Start);
        await seed(database.CreateStore(sqlClock), sqlClock);

        var state = new FullTextIndexState();
        var maintenance = database.CreateFullTextIndexMaintenance(state, TimeProvider.System);
        await maintenance.CheckAsync(Ct);
        if (HasFullText)
        {
            var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
            while (!state.IsReady)
            {
                DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the full-text index did not become ready");
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                await maintenance.CheckAsync(Ct);
            }
        }

        return new Corpus(reference, database.CreateStore(sqlClock), database.CreateStore(sqlClock, state), state.IsReady);
    }

    private static async Task SeedWordsAsync(IMemoryStore store, FakeTimeProvider clock)
    {
        await store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(Workspace, Session, null, null, null, Ct);
        string[] contents =
        [
            "Lemon and LIME", "lemon lemon lime", "only lemon here", "lime then lemon", "orange lemon lime lemon",
            "kiwi alone", "the apple is red", "near and far", "東京 is big", "東京タワーに行きました",
            "100% sure", "100 percent", "a_b literal", "axb literal", "[x] marks", "x marks", "c:\\dir path", "lemonade stand",
            "the cat and the hat",
        ];
        await store.Messages.AppendAsync(
            Workspace,
            Session,
            [.. contents.Select((content, i) => Msg(i % 2 == 0 ? "bob" : "carol", content, Start.AddMinutes(i % 5)))],
            idempotency: null,
            Ct);
    }

    /// <summary>
    /// Two sessions with chunked, embedded messages: alice is a member of <see cref="Session"/> from minute 10 to 20 and
    /// of <see cref="Other"/> from minute 30; bob and carol send.
    /// </summary>
    private static async Task SeedVectorsAsync(IMemoryStore store, FakeTimeProvider clock)
    {
        await store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(Workspace, Session, null, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(Workspace, Other, null, null, null, Ct);
        var vectors = new List<float[]>();

        async Task AppendAsync(string session, string peer, string content, int minute, params float[][] chunkVectors)
        {
            await store.Messages.AppendAsync(
                Workspace,
                session,
                new MessageAppend(
                    [Msg(peer, content, Start.AddMinutes(minute))],
                    Chunks: [[.. chunkVectors.Select((_, i) => $"{content} part {i}")]]),
                Ct);
            vectors.AddRange(chunkVectors);
        }

        await AppendAsync(Session, "bob", "zebra before joining", 5, Vector(0), Vector(1));
        clock.SetUtcNow(Start.AddMinutes(10));
        await store.Sessions.AddPeersAsync(Workspace, Session, new Dictionary<string, SessionPeerConfig> { ["alice"] = new() }, Ct);
        await AppendAsync(Session, "carol", "zebra at joining", 10, Vector(0, 1, 0.5f));
        await AppendAsync(Session, "bob", "zebra while member", 15, Vector(2), Vector(0, 2, 0.2f));
        await AppendAsync(Session, "carol", "zebra tied", 15, Vector(0, 1, 0.5f));
        clock.SetUtcNow(Start.AddMinutes(20));
        await store.Sessions.RemovePeersAsync(Workspace, Session, ["alice"], Ct);
        await AppendAsync(Session, "bob", "zebra at leaving", 20, Vector(1));
        await AppendAsync(Other, "bob", "zebra elsewhere before rejoining", 25, Vector(2));
        clock.SetUtcNow(Start.AddMinutes(30));
        await store.Sessions.AddPeersAsync(Workspace, Other, new Dictionary<string, SessionPeerConfig> { ["alice"] = new() }, Ct);
        await AppendAsync(Other, "carol", "zebra elsewhere after rejoining", 35, Vector(2, 0, 0.7f), Vector(1, 2, 0.1f));
        await AppendAsync(Other, "bob", "zebra not embedded", 36);

        // Chunks are embedded in append order; content identifies each one in both stores.
        var pending = (await store.Embeddings.ListPendingAsync(1000, Ct)).ToDictionary(p => p.Content);
        var order = 0;
        foreach (var content in new[]
        {
            "zebra before joining", "zebra at joining", "zebra while member", "zebra tied", "zebra at leaving",
            "zebra elsewhere before rejoining", "zebra elsewhere after rejoining",
        })
        {
            for (var i = 0; pending.TryGetValue($"{content} part {i}", out var chunk); i++)
            {
                await store.Embeddings.SaveAsync(chunk, vectors[order++], "test-model", Ct);
            }
        }

        order.ShouldBe(vectors.Count);
    }

    /// <param name="Like">A SQL store whose lexical search always uses LIKE.</param>
    /// <param name="Ready">A SQL store using the full-text index whenever the maintenance found it ready.</param>
    private sealed record Corpus(IMemoryStore Reference, IMemoryStore Like, IMemoryStore Ready, bool IsFullTextReady);
}

[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlMessageSearchFtsTests(SqlServerFixture fixture) : SqlMessageSearchTests
{
    protected override bool HasFullText => true;

    private protected override Task<SqlTestDatabase> CreateDatabaseAsync() => SqlTestDatabase.CreateAsync(fixture);
}

[Collection(SqlServerNoFtsDockerGroup.Name)]
public sealed class SqlMessageSearchNoFtsTests(SqlServerNoFtsFixture fixture) : SqlMessageSearchTests
{
    protected override bool HasFullText => false;

    private protected override Task<SqlTestDatabase> CreateDatabaseAsync() => SqlTestDatabase.CreateAsync(fixture);
}
