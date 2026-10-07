using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task AddInMemoryMemoryStore_RegistersSingletonOnRegisteredClock()
    {
        var clock = new FakeTimeProvider(Start);
        using var provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddInMemoryMemoryStore()
            .BuildServiceProvider();

        var store = provider.GetRequiredService<IMemoryStore>();

        store.ShouldBeOfType<InMemoryMemoryStore>();
        provider.GetRequiredService<IMemoryStore>().ShouldBeSameAs(store);
        (await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct)).CreatedAt.ShouldBe(Start);
    }

    [Fact]
    public async Task AddInMemoryMemoryStore_WithoutRegisteredClock_UsesSystemClock()
    {
        using var provider = new ServiceCollection().AddInMemoryMemoryStore().BuildServiceProvider();
        var before = DateTimeOffset.UtcNow;

        var created = await provider.GetRequiredService<IMemoryStore>().Workspaces.GetOrCreateAsync("ws", null, null, Ct);

        created.CreatedAt.ShouldBeInRange(before, DateTimeOffset.UtcNow);
    }
}
