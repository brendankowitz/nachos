using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// Behavioural contract of <see cref="IMessageSearch"/>: scopes, filters and ranking of both candidate lists. A provider's
/// test project derives from this class and supplies the store.
/// </summary>
public abstract class SearchContractTests : M3ContractTestBase
{
    private static MessageSearchRequest Request(string workspace, MessageSearchScope? scope = null, FilterNode? filter = null) =>
        new(workspace, scope ?? new MessageSearchScope.Workspace(), filter);

    private static async Task<List<string>> LexicalAsync(IMemoryStore store, MessageSearchRequest request, params string[] terms) =>
        [.. (await store.Search.LexicalAsync(request, terms, 100, Ct)).Select(hit => hit.Message.Content)];

    private static async Task<List<string>> VectorAsync(IMemoryStore store, MessageSearchRequest request, float[] query, int take = 100) =>
        [.. (await store.Search.VectorAsync(request, query, take, Ct)).Select(hit => hit.Message.Content)];

    /// <summary>Appends one message whose single chunk is its content, embedded as <paramref name="vector"/>.</summary>
    private static async Task<MessageRecord> AppendEmbeddedAsync(
        IMemoryStore store, string workspace, string session, NewMessage message, float[] vector)
    {
        var record = (await store.Messages.AppendAsync(
            workspace, session, new MessageAppend([message], Chunks: [[message.Content]]), Ct)).Single();
        await EmbedChunksAsync(store, new Dictionary<(string, int), float[]> { [(record.PublicId, 0)] = vector });
        return record;
    }

    [Fact]
    public async Task Search_SessionScope_ExcludesOtherSessions()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var other = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, other, null, null, null, Ct);
        await AppendEmbeddedAsync(store, workspace, session, Msg("bob", "apple in session"), Vector(0));
        await AppendEmbeddedAsync(store, workspace, other, Msg("bob", "apple elsewhere"), Vector(0));

        var scoped = Request(workspace, new MessageSearchScope.Session(session));
        (await LexicalAsync(store, scoped, "apple")).ShouldBe(["apple in session"]);
        (await VectorAsync(store, scoped, Vector(0))).ShouldBe(["apple in session"]);
        (await LexicalAsync(store, Request(workspace), "apple")).Count.ShouldBe(2);
        (await LexicalAsync(store, Request(workspace, new MessageSearchScope.Session(Unique("missing"))), "apple")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_PeerPerspective_RespectsMembershipWindows()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, first) = await NewSessionAsync(store);
        var second = Unique("sess");
        var never = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, second, null, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, never, null, null, null, Ct);

        // alice is a member of `first` from t1 to t2, then of `second` from t3 on; she sends nothing.
        var t1 = Start.AddMinutes(10);
        var t2 = Start.AddMinutes(20);
        var t3 = Start.AddMinutes(30);
        await AppendEmbeddedAsync(store, workspace, first, Msg("bob", "zebra before joining", Start.AddMinutes(5)), Vector(0));
        clock.SetUtcNow(t1);
        await store.Sessions.AddPeersAsync(workspace, first, Members("alice"), Ct);
        await AppendEmbeddedAsync(store, workspace, first, Msg("bob", "zebra at joining", t1), Vector(0));
        await AppendEmbeddedAsync(store, workspace, first, Msg("bob", "zebra while member", Start.AddMinutes(15)), Vector(0));
        clock.SetUtcNow(t2);
        await store.Sessions.RemovePeersAsync(workspace, first, ["alice"], Ct);
        await AppendEmbeddedAsync(store, workspace, first, Msg("bob", "zebra at leaving", t2), Vector(0));
        await AppendEmbeddedAsync(store, workspace, second, Msg("bob", "zebra elsewhere before rejoining", Start.AddMinutes(25)), Vector(0));
        clock.SetUtcNow(t3);
        await store.Sessions.AddPeersAsync(workspace, second, Members("alice"), Ct);
        await AppendEmbeddedAsync(store, workspace, second, Msg("bob", "zebra elsewhere after rejoining", Start.AddMinutes(35)), Vector(0));
        await AppendEmbeddedAsync(store, workspace, never, Msg("bob", "zebra never attended", Start.AddMinutes(35)), Vector(0));

        var perspective = Request(workspace, new MessageSearchScope.PeerPerspective("alice"));
        string[] visible = ["zebra elsewhere after rejoining", "zebra while member", "zebra at joining"];
        (await LexicalAsync(store, perspective, "zebra")).ShouldBe(visible);
        (await VectorAsync(store, perspective, Vector(0))).ShouldBe(visible);
        (await LexicalAsync(store, Request(workspace, new MessageSearchScope.PeerPerspective(Unique("nobody"))), "zebra")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_Filter_UsesMessageAllowlist()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await AppendEmbeddedAsync(store, workspace, session, Msg("bob", "kiwi from bob"), Vector(0));
        await AppendEmbeddedAsync(store, workspace, session, Msg("carol", "kiwi from carol"), Vector(0));
        await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([new NewMessage("bob", "kiwi tagged", 3, new JsonObject { ["topic"] = "fruit" }, null)]),
            Ct);

        var byPeer = FilterParser.Parse(new JsonObject { ["peer_id"] = "carol" }, ResourceKind.Message);
        var byMetadata = FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { ["topic"] = "fruit" } }, ResourceKind.Message);

        (await LexicalAsync(store, Request(workspace, filter: byPeer), "kiwi")).ShouldBe(["kiwi from carol"]);
        (await VectorAsync(store, Request(workspace, filter: byPeer), Vector(0))).ShouldBe(["kiwi from carol"]);
        (await LexicalAsync(store, Request(workspace, filter: byMetadata), "kiwi")).ShouldBe(["kiwi tagged"]);
        (await VectorAsync(store, Request(workspace, filter: byMetadata), Vector(0))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Lexical_AnyTermCaseInsensitive_RankedByTermsMatchedThenRecency()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend(
            [
                Msg("bob", "Lemon and LIME", Start.AddMinutes(1)),
                Msg("bob", "lemon lemon lime", Start.AddMinutes(2)),
                Msg("bob", "only lemon here", Start.AddMinutes(3)),
                Msg("bob", "lime, then lemon", Start.AddMinutes(4)),
                Msg("bob", "100% [lemon_lime]", Start.AddMinutes(5)),
                Msg("bob", "no citrus at all", Start.AddMinutes(6)),
            ]),
            Ct);

        var hits = await store.Search.LexicalAsync(Request(workspace), ["lemon", "lime"], 100, Ct);

        // Occurrences do not count: "lemon lemon lime" matches two terms, like the newer messages ahead of it.
        hits.Select(hit => hit.Message.Content).ShouldBe(
            ["100% [lemon_lime]", "lime, then lemon", "lemon lemon lime", "Lemon and LIME", "only lemon here"]);
        hits.Select(hit => hit.Rank).ShouldBe([1, 2, 3, 4, 5]);
        (await store.Search.LexicalAsync(Request(workspace), ["lemon", "lime"], 2, Ct)).Select(hit => hit.Message.Content)
            .ShouldBe(["100% [lemon_lime]", "lime, then lemon"]);

        // Terms equal but for case count once, so "only" does not outweigh "lime".
        (await LexicalAsync(store, Request(workspace), "only", "ONLY", "Only", "lime")).ShouldBe(
            ["100% [lemon_lime]", "lime, then lemon", "only lemon here", "lemon lemon lime", "Lemon and LIME"]);

        (await LexicalAsync(store, Request(workspace))).ShouldBeEmpty();
        (await LexicalAsync(store, Request(workspace), "%")).ShouldBe(["100% [lemon_lime]"]);
        (await LexicalAsync(store, Request(workspace), new string('%', IMessageSearch.MaxTermLength))).ShouldBeEmpty();
        await Should.ThrowAsync<ArgumentException>(
            () => store.Search.LexicalAsync(Request(workspace), [new string('a', IMessageSearch.MaxTermLength + 1)], 10, Ct));
        await Should.ThrowAsync<ArgumentException>(() => store.Search.LexicalAsync(Request(workspace), [""], 10, Ct));
        await Should.ThrowAsync<ArgumentException>(() => store.Search.LexicalAsync(Request(workspace), ["lemon", ""], 10, Ct));
    }

    [Fact]
    public async Task Lexical_AnyTerm_FindsAMessageWithOnlyOneOfTheTerms()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("bob", "I like lemons", Start)]), Ct);

        (await LexicalAsync(store, Request(workspace), "what", "user", "say", "lemons")).ShouldBe(["I like lemons"]);
        (await LexicalAsync(store, Request(workspace), "what", "user")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Lexical_MoreTermsMatched_OutranksANewerMessage()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([Msg("bob", "a red apple", Start.AddMinutes(1)), Msg("bob", "a red car", Start.AddMinutes(2))]),
            Ct);

        (await LexicalAsync(store, Request(workspace), "red", "apple")).ShouldBe(["a red apple", "a red car"]);
        (await LexicalAsync(store, Request(workspace), "red")).ShouldBe(["a red car", "a red apple"]);
    }

    [Fact]
    public async Task Vector_RanksByBestChunk_AndSkipsUnembedded()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var appended = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend(
                [Msg("bob", "two chunks", Start.AddMinutes(1)), Msg("bob", "near", Start.AddMinutes(2)), Msg("bob", "tied", Start.AddMinutes(3)), Msg("bob", "pending", Start.AddMinutes(4))],
                Chunks: [["far part", "exact part"], ["near"], ["tied"], ["pending"]]),
            Ct);
        await EmbedChunksAsync(store, new Dictionary<(string, int), float[]>
        {
            [(appended[0].PublicId, 0)] = Vector(1),
            [(appended[0].PublicId, 1)] = Vector(0),
            [(appended[1].PublicId, 0)] = Vector(0, 1, 0.5f),
            [(appended[2].PublicId, 0)] = Vector(0, 1, 0.5f),
        });

        var hits = await store.Search.VectorAsync(Request(workspace), Vector(0), 100, Ct);

        hits.Select(hit => hit.Message.Content).ShouldBe(["two chunks", "tied", "near"]);
        hits.Select(hit => hit.Rank).ShouldBe([1, 2, 3]);
        (await VectorAsync(store, Request(workspace), Vector(0), take: 1)).ShouldBe(["two chunks"]);
        await Should.ThrowAsync<ArgumentException>(() => store.Search.VectorAsync(Request(workspace), new float[2], 10, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.Search.VectorAsync(Request(workspace), Vector(0), 0, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Search.LexicalAsync(Request(Unique("ws")), ["x"], 10, Ct));
    }

    [Fact]
    public async Task Vector_PendingAndFailedChunks_BesideSyncedChunk_UseSyncedOnly()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend(
                [Msg("bob", "mixed", Start.AddMinutes(1)), Msg("bob", "synced only", Start.AddMinutes(2)), Msg("bob", "never synced", Start.AddMinutes(3))],
                Chunks: [["mixed far", "mixed pending", "mixed failed"], ["synced near"], ["never pending", "never failed"]]),
            Ct);
        var pending = (await store.Embeddings.ListPendingAsync(100_000, Ct)).Where(p => p.WorkspaceName == workspace).ToList();
        await store.Embeddings.SaveAsync(pending.Single(p => p.Content == "mixed far"), Vector(1), "test-model", Ct);
        await store.Embeddings.SaveAsync(pending.Single(p => p.Content == "synced near"), Vector(0, 1, 0.5f), "test-model", Ct);
        await store.Embeddings.MarkFailedAsync(pending.Single(p => p.Content == "mixed failed"), Ct);
        await store.Embeddings.MarkFailedAsync(pending.Single(p => p.Content == "never failed"), Ct);

        // "mixed" ranks by its one synced chunk (far), not by a pending or failed one; "never synced" is no candidate.
        (await VectorAsync(store, Request(workspace), Vector(0))).ShouldBe(["synced only", "mixed"]);
    }
}
