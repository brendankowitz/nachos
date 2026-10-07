using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Shouldly;

namespace Nachos.DataLayer.InMemory.Tests;

/// <summary>Behaviour specific to the in-memory provider, beyond what <c>StoreContractTests</c> pins.</summary>
public sealed class InMemoryMemoryStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => CancellationToken.None;

    private static NewMessage Msg(string peer, string content, DateTimeOffset? createdAt = null) =>
        new(peer, content, 1, null, createdAt);

    private static async Task<(InMemoryMemoryStore Store, FakeTimeProvider Clock)> NewSessionStoreAsync()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new InMemoryMemoryStore(clock);
        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        await store.Sessions.GetOrCreateAsync("ws", "s", null, null, null, Ct);
        return (store, clock);
    }

    /// <summary>Starts <paramref name="count"/> operations behind one gate so they genuinely overlap.</summary>
    private static async Task<T[]> RunConcurrentlyAsync<T>(int count, Func<int, Task<T>> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await operation(i);
            }))
            .ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Instances_DoNotShareState()
    {
        var first = new InMemoryMemoryStore(TimeProvider.System);
        var second = new InMemoryMemoryStore(TimeProvider.System);

        await first.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        await first.Grants.AddAsync(new GrantRecord("obj", null, GrantRoles.Admin), Ct);

        (await second.Workspaces.GetAsync("ws", Ct)).ShouldBeNull();
        (await second.Workspaces.ListAsync(null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await second.Grants.ListAsync(null, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Append_ConcurrentBatches_AllocateContiguousSeqWithoutGaps()
    {
        var (store, _) = await NewSessionStoreAsync();

        var batches = await RunConcurrentlyAsync(
            32, i => store.Messages.AppendAsync("ws", "s", [Msg("alice", $"{i}-a"), Msg("alice", $"{i}-b"), Msg("alice", $"{i}-c")], null, Ct));

        // Each batch is contiguous and in request order, and together they cover 1..96 exactly once.
        foreach (var batch in batches)
        {
            batch.Select(m => m.Seq).ShouldBe([batch[0].Seq, batch[0].Seq + 1, batch[0].Seq + 2]);
            batch.Select(m => m.Content[^1]).ShouldBe(['a', 'b', 'c']);
        }

        batches.SelectMany(b => b).Select(m => m.Seq).Order().ShouldBe(Enumerable.Range(1, 96).Select(i => (long)i));
        var listed = await store.Messages.ListAsync("ws", "s", null, new PageRequest(1, 100), Ct);
        listed.Total.ShouldBe(96);
        listed.Items.Select(m => m.Seq).ShouldBe(Enumerable.Range(1, 96).Select(i => (long)i));
    }

    [Fact]
    public async Task Append_ConcurrentNewSenders_CreateEachPeerAndMembershipOnce()
    {
        var (store, _) = await NewSessionStoreAsync();

        await RunConcurrentlyAsync(32, i => store.Messages.AppendAsync("ws", "s", [Msg($"p{i % 4}", "hi")], null, Ct));

        (await store.Peers.ListAsync("ws", PeerKind.All, null, new PageRequest(), Ct)).Items.Select(p => p.Name)
            .ShouldBe(["p0", "p1", "p2", "p3"], ignoreOrder: true);
        (await store.Sessions.ListPeersAsync("ws", "s", new PageRequest(), Ct)).Total.ShouldBe(4);
    }

    [Fact]
    public async Task ConcurrentWorkspaceAndPeerCreation_AcrossWorkspaces_IsConsistent()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);

        // Store-level (workspace) and workspace-level (peer) writes interleave with listings.
        await RunConcurrentlyAsync(32, async i =>
        {
            await store.Workspaces.GetOrCreateAsync($"ws{i % 8}", null, null, Ct);
            await store.Peers.GetOrCreateAsync($"ws{i % 8}", $"p{i}", null, null, Ct);
            return (await store.Workspaces.ListAsync(null, new PageRequest(), Ct)).Total;
        });

        (await store.Workspaces.ListAsync(null, new PageRequest(), Ct)).Total.ShouldBe(8);
        for (var w = 0; w < 8; w++)
        {
            (await store.Peers.ListAsync($"ws{w}", PeerKind.All, null, new PageRequest(), Ct)).Total.ShouldBe(4);
        }
    }

    [Fact]
    public async Task Append_SerializerThrows_RollsBackReactivationAndNewPeers()
    {
        var (store, _) = await NewSessionStoreAsync();
        await store.Sessions.AddPeersAsync("ws", "s", new Dictionary<string, SessionPeerConfig> { ["left"] = new(true, false) }, Ct);
        await store.Sessions.RemovePeersAsync("ws", "s", ["left"], Ct);
        var faulty = new IdempotencyWrite(
            "key", new string('a', 64), 201, _ => throw new InvalidOperationException("boom"), TimeSpan.FromMinutes(1));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => store.Messages.AppendAsync("ws", "s", [Msg("left", "x"), Msg("new", "y")], faulty, Ct));

        thrown.Message.ShouldBe("boom");
        (await store.Sessions.IsActiveMemberAsync("ws", "s", "left", Ct)).ShouldBeFalse();
        (await store.Peers.GetAsync("ws", "new", Ct)).ShouldBeNull();
        (await store.Idempotency.TryGetAsync("ws", "key", Ct)).ShouldBeNull();
        (await store.Messages.AppendAsync("ws", "s", [Msg("left", "z")], null, Ct))[0].Seq.ShouldBe(1);
        (await store.Sessions.GetPeerConfigAsync("ws", "s", "left", Ct)).ShouldBe(new SessionPeerConfig(true, false));
    }

    [Fact]
    public async Task Append_AutoCreatedPeerAndMembership_UseStoreClockNotMessageTime()
    {
        var (store, clock) = await NewSessionStoreAsync();
        clock.Advance(TimeSpan.FromHours(1));

        var appended = await store.Messages.AppendAsync("ws", "s", [Msg("alice", "old", Start.AddYears(-1))], null, Ct);

        appended[0].CreatedAt.ShouldBe(Start.AddYears(-1));
        (await store.Peers.GetAsync("ws", "alice", Ct))!.CreatedAt.ShouldBe(Start.AddHours(1));
        (await store.Sessions.GetPeerConfigAsync("ws", "s", "alice", Ct)).ShouldBe(new SessionPeerConfig());
    }

    [Fact]
    public async Task MetadataFilter_NumbersBuiltInCSharp_CompareNumerically()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        await store.Peers.GetOrCreateAsync("ws", "int", new JsonObject { ["n"] = 5, ["tags"] = new JsonArray(7, "x") }, null, Ct);
        await store.Peers.GetOrCreateAsync("ws", "decimal", new JsonObject { ["n"] = 4.5m }, null, Ct);
        await store.Peers.GetOrCreateAsync("ws", "string", new JsonObject { ["n"] = "5" }, null, Ct);

        async Task<string[]> MatchAsync(string filter) =>
            [.. (await store.Peers.ListAsync("ws", PeerKind.All, FilterParser.Parse(filter, ResourceKind.Peer), new PageRequest(), Ct))
                .Items.Select(p => p.Name)];

        (await MatchAsync("""{"metadata":{"n":5.0}}""")).ShouldBe(["int"]);
        (await MatchAsync("""{"metadata":{"n":{"gt":4}}}""")).ShouldBe(["int", "decimal"]);
        (await MatchAsync("""{"metadata":{"n":{"lt":"6"}}}""")).ShouldBe(["string"]);
        (await MatchAsync("""{"metadata":{"tags":[7.0]}}""")).ShouldBe(["int"]);
    }

    [Fact]
    public async Task CanceledToken_FaultsWithoutSideEffects()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => store.Workspaces.GetOrCreateAsync("ws", null, null, cts.Token));

        (await store.Workspaces.GetAsync("ws", Ct)).ShouldBeNull();
    }

    // ---------------------------------------------------------------- serializer re-entrancy

    private const string SerializerModifiedStore =
        "The response serializer modified the store; it must be a pure function of the stored messages.";

    public static TheoryData<string> Interferences() => new() { "append", "create-sender-peer", "reactivate-sender" };

    /// <summary>A serializer that writes to the store invalidates what the append staged, so nothing of it may commit.</summary>
    [Theory]
    [MemberData(nameof(Interferences))]
    public async Task Append_SerializerThatWritesToTheStore_IsRejectedBeforeAnythingCommits(string interference)
    {
        var (store, _) = await NewSessionStoreAsync();
        await store.Sessions.AddPeersAsync("ws", "s", new Dictionary<string, SessionPeerConfig> { ["left"] = new(true, false) }, Ct);
        await store.Sessions.RemovePeersAsync("ws", "s", ["left"], Ct);
        var reentrant = new IdempotencyWrite(
            "key",
            new string('a', 64),
            201,
            _ =>
            {
                Interfere(store, interference);
                return "body";
            },
            TimeSpan.FromMinutes(1));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => store.Messages.AppendAsync("ws", "s", [Msg("outer", "outer-message"), Msg("left", "left-message")], reentrant, Ct));

        thrown.Message.ShouldBe(SerializerModifiedStore);
        var stored = (await store.Messages.ListAsync("ws", "s", null, new PageRequest(), Ct)).Items;
        stored.ShouldNotContain(m => m.Content == "outer-message" || m.Content == "left-message");
        stored.Select(m => m.Seq).ShouldBe(Enumerable.Range(1, stored.Count).Select(i => (long)i));
        (await store.Idempotency.TryGetAsync("ws", "key", Ct)).ShouldBeNull();
        if (interference != "reactivate-sender")
        {
            (await store.Sessions.IsActiveMemberAsync("ws", "s", "left", Ct)).ShouldBeFalse();
        }

        if (interference == "append")
        {
            (await store.Peers.GetAsync("ws", "outer", Ct)).ShouldBeNull();
            (await store.Sessions.IsActiveMemberAsync("ws", "s", "outer", Ct)).ShouldBeFalse();
        }

        (await store.Messages.AppendAsync("ws", "s", [Msg("next", "after")], null, Ct))[0].Seq.ShouldBe(stored.Count + 1);
    }

    [Fact]
    public async Task Append_SerializerThatAppendsElsewhereWithTheSameKey_IsRejectedAndKeepsTheInnerRecord()
    {
        // alice is already an active member of both sessions, so nothing but the idempotency record differs afterwards.
        var (store, _) = await NewSessionStoreAsync();
        await store.Sessions.GetOrCreateAsync("ws", "other", null, null, null, Ct);
        await store.Sessions.AddPeersAsync("ws", "s", Members("alice"), Ct);
        await store.Sessions.AddPeersAsync("ws", "other", Members("alice"), Ct);
        var inner = new IdempotencyWrite("key", new string('b', 64), 201, _ => "inner-body", TimeSpan.FromMinutes(1));
        var outer = new IdempotencyWrite(
            "key",
            new string('a', 64),
            201,
            _ =>
            {
                store.Messages.AppendAsync("ws", "other", [Msg("alice", "inner-message")], inner, Ct).GetAwaiter().GetResult();
                return "outer-body";
            },
            TimeSpan.FromMinutes(1));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => store.Messages.AppendAsync("ws", "s", [Msg("alice", "outer-message")], outer, Ct));

        thrown.Message.ShouldBe(SerializerModifiedStore);
        (await store.Messages.ListAsync("ws", "s", null, new PageRequest(), Ct)).Total.ShouldBe(0);
        var record = (await store.Idempotency.TryGetAsync("ws", "key", Ct))!;
        record.RequestHash.ShouldBe(new string('b', 64));
        record.ResponseBody.ShouldBe("inner-body");
        (await store.Messages.ListAsync("ws", "other", null, new PageRequest(), Ct)).Items.Select(m => m.Content)
            .ShouldBe(["inner-message"]);
    }

    [Fact]
    public async Task Append_SerializerThatRemovesAnActiveSender_IsRejectedBeforeAnythingCommits()
    {
        var (store, _) = await NewSessionStoreAsync();
        await store.Sessions.AddPeersAsync("ws", "s", Members("alice"), Ct);
        var removing = new IdempotencyWrite(
            "key",
            new string('a', 64),
            201,
            _ =>
            {
                store.Sessions.RemovePeersAsync("ws", "s", ["alice"], Ct).GetAwaiter().GetResult();
                return "body";
            },
            TimeSpan.FromMinutes(1));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => store.Messages.AppendAsync("ws", "s", [Msg("alice", "outer-message")], removing, Ct));

        thrown.Message.ShouldBe(SerializerModifiedStore);
        (await store.Messages.ListAsync("ws", "s", null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Idempotency.TryGetAsync("ws", "key", Ct)).ShouldBeNull();
        // The removal the serializer made stands; the append did not silently reactivate the sender.
        (await store.Sessions.IsActiveMemberAsync("ws", "s", "alice", Ct)).ShouldBeFalse();
        (await store.Messages.AppendAsync("ws", "s", [Msg("alice", "after")], null, Ct))[0].Seq.ShouldBe(1);
    }

    /// <summary>Called from inside a serializer, where blocking is unavoidable; the store completes synchronously.</summary>
    private static void Interfere(InMemoryMemoryStore store, string interference)
    {
        var write = interference switch
        {
            "append" => store.Messages.AppendAsync("ws", "s", [Msg("inner", "inner-message")], null, Ct),
            "create-sender-peer" => store.Peers.GetOrCreateAsync("ws", "outer", null, null, Ct),
            _ => store.Sessions.AddPeersAsync("ws", "s", new Dictionary<string, SessionPeerConfig> { ["left"] = new(false, false) }, Ct),
        };
        write.GetAwaiter().GetResult();
    }

    // ---------------------------------------------------------------- get-or-create races

    /// <summary>
    /// Runs <paramref name="count"/> calls on dedicated threads released together, for more real overlap than the thread
    /// pool gives. The store reports failures through its tasks, so the threads never throw.
    /// </summary>
    private static async Task<T[]> RaceOnThreadsAsync<T>(int count, Func<int, Task<T>> operation)
    {
        var tasks = new Task<T>[count];
        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, count)
            .Select(i => new Thread(() =>
            {
                start.Wait();
                tasks[i] = operation(i);
            }))
            .ToList();
        threads.ForEach(thread => thread.Start());
        start.Set();
        threads.ForEach(thread => thread.Join());
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task GetOrCreateWorkspace_Repeated32WayRace_YieldsOneRowAndIdenticalResults()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var store = new InMemoryMemoryStore(TimeProvider.System);

            var results = await RaceOnThreadsAsync(
                32, i => store.Workspaces.GetOrCreateAsync("ws", new JsonObject { ["caller"] = i }, null, Ct));

            results.Select(w => w.Metadata.ToJsonString()).Distinct().Count().ShouldBe(1);
            results.Select(w => w.CreatedAt).Distinct().Count().ShouldBe(1);
            (await store.Workspaces.ListAsync(null, new PageRequest(), Ct)).Total.ShouldBe(1);
            (await store.Workspaces.GetAsync("ws", Ct))!.Metadata.ToJsonString().ShouldBe(results[0].Metadata.ToJsonString());
        }
    }

    [Fact]
    public async Task GetOrCreatePeer_Repeated32WayRace_YieldsOneRowAndIdenticalResults()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var store = new InMemoryMemoryStore(TimeProvider.System);
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);

            var results = await RaceOnThreadsAsync(
                32, i => store.Peers.GetOrCreateAsync("ws", "alice", new JsonObject { ["caller"] = i }, null, Ct));

            results.Select(p => p.Metadata.ToJsonString()).Distinct().Count().ShouldBe(1);
            results.Select(p => p.CreatedAt).Distinct().Count().ShouldBe(1);
            (await store.Peers.ListAsync("ws", PeerKind.All, null, new PageRequest(), Ct)).Total.ShouldBe(1);
            (await store.Peers.GetAsync("ws", "alice", Ct))!.Metadata.ToJsonString().ShouldBe(results[0].Metadata.ToJsonString());
        }
    }

    // ---------------------------------------------------------------- ordering under a frozen clock

    private static Dictionary<string, SessionPeerConfig> Members(params string[] names) =>
        names.ToDictionary(name => name, _ => new SessionPeerConfig(true, true));

    [Fact]
    public async Task ListPeers_FrozenClock_UsesPeerCreationOrderNotJoinOrder()
    {
        // The clock never moves, so only the insertion tiebreak orders the peers. Names are deliberately not sorted.
        var (store, _) = await NewSessionStoreAsync();
        string[] created = ["p-zulu", "p-alpha", "p-mike", "p-bravo"];
        foreach (var name in created)
        {
            await store.Peers.GetOrCreateAsync("ws", name, null, null, Ct);
        }

        // Join in a different order than creation, through both membership paths.
        await store.Sessions.AddPeersAsync("ws", "s", Members("p-mike", "p-zulu"), Ct);
        await store.Messages.AppendAsync("ws", "s", [Msg("p-bravo", "x"), Msg("p-alpha", "y")], null, Ct);

        async Task<string[]> ListAsync(PageRequest page) =>
            [.. (await store.Sessions.ListPeersAsync("ws", "s", page, Ct)).Items.Select(p => p.Name)];

        (await ListAsync(new PageRequest())).ShouldBe(created);
        (await ListAsync(new PageRequest(1, 50, true))).ShouldBe(created.Reverse());
        (await ListAsync(new PageRequest(1, 3))).ShouldBe(created[..3]);
        (await ListAsync(new PageRequest(2, 3))).ShouldBe(created[3..]);

        // A peer that leaves and rejoins keeps its original position.
        await store.Sessions.RemovePeersAsync("ws", "s", ["p-zulu"], Ct);
        (await ListAsync(new PageRequest())).ShouldBe(created[1..]);
        await store.Sessions.AddPeersAsync("ws", "s", Members("p-zulu"), Ct);
        (await ListAsync(new PageRequest())).ShouldBe(created);
    }

    [Fact]
    public async Task ListSessionsForPeer_FrozenClock_UsesSessionCreationOrderNotJoinOrder()
    {
        var store = new InMemoryMemoryStore(new FakeTimeProvider(Start));
        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        string[] created = ["s-zulu", "s-alpha", "s-mike", "s-bravo"];
        foreach (var name in created)
        {
            await store.Sessions.GetOrCreateAsync("ws", name, null, null, null, Ct);
        }

        foreach (var name in new[] { "s-mike", "s-bravo", "s-zulu" })
        {
            await store.Sessions.AddPeersAsync("ws", name, Members("alice"), Ct);
        }

        async Task<string[]> ListAsync(PageRequest page) =>
            [.. (await store.Peers.ListSessionsForPeerAsync("ws", "alice", null, page, Ct)).Items.Select(s => s.Name)];

        string[] expected = ["s-zulu", "s-mike", "s-bravo"];
        (await ListAsync(new PageRequest())).ShouldBe(expected);
        (await ListAsync(new PageRequest(1, 50, true))).ShouldBe(expected.Reverse());

        // Leaving and rejoining keeps the session's place.
        await store.Sessions.RemovePeersAsync("ws", "s-zulu", ["alice"], Ct);
        (await ListAsync(new PageRequest())).ShouldBe(expected[1..]);
        await store.Sessions.AddPeersAsync("ws", "s-zulu", Members("alice"), Ct);
        (await ListAsync(new PageRequest())).ShouldBe(expected);
    }

    // ---------------------------------------------------------------- paging, numbers, idempotency keys

    [Fact]
    public async Task Paging_ReportsCeilingPageCount_AndZeroPagesWhenEmpty()
    {
        var (store, _) = await NewSessionStoreAsync();

        var empty = await store.Messages.ListAsync("ws", "s", null, new PageRequest(1, 2), Ct);
        empty.Items.ShouldBeEmpty();
        empty.Total.ShouldBe(0);
        empty.Pages.ShouldBe(0);
        empty.PageNumber.ShouldBe(1);
        empty.Size.ShouldBe(2);
        (await store.Peers.ListAsync("ws", PeerKind.All, null, new PageRequest(3, 7), Ct)).Pages.ShouldBe(0);

        await store.Messages.AppendAsync("ws", "s", [.. Enumerable.Range(0, 5).Select(i => Msg("alice", $"m{i}"))], null, Ct);

        var last = await store.Messages.ListAsync("ws", "s", null, new PageRequest(3, 2), Ct);
        last.Items.Select(m => m.Content).ShouldBe(["m4"]);
        (last.Total, last.Pages).ShouldBe((5L, 3));
        var beyond = await store.Messages.ListAsync("ws", "s", null, new PageRequest(4, 2), Ct);
        beyond.Items.ShouldBeEmpty();
        (beyond.Total, beyond.Pages, beyond.PageNumber).ShouldBe((5L, 3, 4));
        (await store.Messages.ListAsync("ws", "s", null, new PageRequest(1, 5), Ct)).Pages.ShouldBe(1);
        (await store.Messages.ListAsync("ws", "s", null, new PageRequest(1, 4), Ct)).Pages.ShouldBe(2);
    }

    [Fact]
    public async Task MetadataNumbers_CompareByExactValue_NotThroughDouble()
    {
        // FilterOp remarks: "Numbers compare by numeric value". These two values differ, yet are the same double.
        const string Precise = "0.1000000000000000055511151231258";
        ((double)decimal.Parse(Precise, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(0.1);
        var store = new InMemoryMemoryStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        await store.Peers.GetOrCreateAsync("ws", "precise", (JsonObject)JsonNode.Parse($$"""{"n":{{Precise}}}""")!, null, Ct);
        await store.Peers.GetOrCreateAsync("ws", "tenth", (JsonObject)JsonNode.Parse("""{"n":0.1}""")!, null, Ct);

        async Task<string[]> MatchAsync(string filter) =>
            [.. (await store.Peers.ListAsync("ws", PeerKind.All, FilterParser.Parse(filter, ResourceKind.Peer), new PageRequest(), Ct))
                .Items.Select(p => p.Name)];

        (await MatchAsync("""{"metadata":{"n":0.1}}""")).ShouldBe(["tenth"]);
        (await MatchAsync("{\"metadata\":{\"n\":" + Precise + "}}")).ShouldBe(["precise"]);
        (await MatchAsync("""{"metadata":{"n":{"gt":0.1}}}""")).ShouldBe(["precise"]);
        (await MatchAsync("{\"metadata\":{\"n\":{\"lt\":" + Precise + "}}}")).ShouldBe(["tenth"]);
    }

    [Fact]
    public async Task IdempotencyKeys_AreCaseSensitive()
    {
        var (store, _) = await NewSessionStoreAsync();
        static IdempotencyWrite Write(string key, char hash) =>
            new(key, new string(hash, 64), 201, messages => messages[0].Content, TimeSpan.FromMinutes(1));

        await store.Messages.AppendAsync("ws", "s", [Msg("alice", "upper")], Write("Key", 'a'), Ct);
        await store.Messages.AppendAsync("ws", "s", [Msg("alice", "lower")], Write("key", 'b'), Ct);

        (await store.Idempotency.TryGetAsync("ws", "Key", Ct))!.RequestHash.ShouldBe(new string('a', 64));
        (await store.Idempotency.TryGetAsync("ws", "key", Ct))!.RequestHash.ShouldBe(new string('b', 64));
        (await store.Idempotency.TryGetAsync("ws", "KEY", Ct)).ShouldBeNull();
        (await store.Messages.ListAsync("ws", "s", null, new PageRequest(), Ct)).Total.ShouldBe(2);
        await Should.ThrowAsync<IdempotencyDuplicateException>(
            () => store.Messages.AppendAsync("ws", "s", [Msg("alice", "again")], Write("key", 'c'), Ct));
    }
}
