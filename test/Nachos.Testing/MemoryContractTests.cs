using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// Behavioural contract of <see cref="IConclusionStore"/>, <see cref="IPeerCardStore"/> and <see cref="IEmbeddingStore"/>.
/// A provider's test project derives from this class and supplies the store.
/// </summary>
/// <remarks>
/// Workspace names are fresh per test. <see cref="IEmbeddingStore"/> and the purges span workspaces, so their
/// assertions only look at this test's conclusions. Leases come from the cross-workspace queue, so a provider whose
/// stores share one database overrides <see cref="ResetQueueAsync"/>, and every derived class must join the non-parallel
/// <see cref="M2StoreContracts"/>.
/// </remarks>
public abstract class MemoryContractTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly ConcurrentBag<IMemoryStore> _stores = [];

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Creates a store whose time-based values come from <paramref name="clock"/>.</summary>
    protected abstract IMemoryStore CreateStore(TimeProvider clock);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    /// <summary>
    /// Empties the work queue (items and leases) of a store that tests share, before each test uses it. A no-op by default,
    /// which suits a provider whose <see cref="CreateStore"/> returns an empty store.
    /// </summary>
    protected virtual Task ResetQueueAsync(IMemoryStore store) => Task.CompletedTask;

    private async Task<(IMemoryStore Store, FakeTimeProvider Clock)> NewStoreAsync()
    {
        var clock = new FakeTimeProvider(Start);
        var store = CreateStore(clock);
        _stores.Add(store);
        await ResetQueueAsync(store);
        return (store, clock);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>A vector along <paramref name="axis"/>, tilted towards <paramref name="towards"/> by <paramref name="tilt"/>.</summary>
    private static ReadOnlyMemory<float> Vector(int axis, int towards = 1, float tilt = 0)
    {
        var vector = new float[VectorSpace.Dimensions];
        vector[axis] = 1;
        vector[towards] += tilt;
        return vector;
    }

    /// <summary>The cosine distance between <c>Vector(axis)</c> and <c>Vector(axis, towards, tilt)</c>.</summary>
    private static double TiltDistance(float tilt) => 1 - (1 / Math.Sqrt(1 + ((double)tilt * tilt)));

    /// <summary>A workspace with peers alice and bob, and a session they are both members of (alice observes others).</summary>
    private static async Task<(string Workspace, string Session)> ArrangeAsync(IMemoryStore store)
    {
        var workspace = Unique("ws");
        var session = Unique("sess");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(
            workspace,
            session,
            null,
            null,
            new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true), ["bob"] = new() },
            Ct);
        return (workspace, session);
    }

    private static NewConclusion Fact(
        string content,
        string? session,
        ReadOnlyMemory<float>? embedding = null,
        string observer = "alice",
        string observed = "bob",
        ConclusionLevel level = ConclusionLevel.Explicit,
        JsonObject? internalMetadata = null) =>
        new(observer, observed, session, level, content, embedding, [], internalMetadata);

    private static async Task<ConclusionRecord> InsertAsync(IMemoryStore store, string workspace, NewConclusion conclusion) =>
        (await store.Conclusions.InsertAsync(workspace, [conclusion], Ct)).Single();

    /// <summary>Appends a message from bob that enqueues work, and leases that work.</summary>
    private static async Task<WorkLease> LeaseAsync(
        IMemoryStore store, string workspace, string session, string owner = "worker", TimeSpan? duration = null)
    {
        await store.Messages.AppendAsync(
            workspace, session, [new NewMessage("bob", "I live in Lisbon.", 5, null, null)], null, [DeriveIntent.Derive], Ct);
        var policy = new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Representation] = WorkBatchPolicy.Immediate };
        return (await store.WorkQueue.TryAcquireAsync(owner, policy, duration ?? Lease, Ct)).ShouldNotBeNull();
    }

    private static async Task<QueueCounts> QueueAsync(IMemoryStore store, string workspace) =>
        (await store.WorkQueue.GetStatusAsync(workspace, null, null, null, Ct)).Totals;

    /// <summary>Every field of a conclusion as text, so two reads compare by value (JSON and lists included).</summary>
    private static string Describe(ConclusionRecord c) =>
        string.Join(
            "|",
            c.PublicId,
            c.WorkspaceName,
            c.ObserverName,
            c.ObservedName,
            c.SessionName ?? "<null>",
            c.Level,
            c.Content,
            c.TimesDerived,
            string.Join(",", c.SourceIds),
            c.InternalMetadata.ToJsonString(),
            c.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<IReadOnlyList<ConclusionRecord>> ListAllAsync(
        IMemoryStore store, string workspace, FilterNode? filter = null, ConclusionOrder order = ConclusionOrder.CreatedAt, bool reverse = false) =>
        (await store.Conclusions.ListAsync(workspace, filter, order, new PageRequest(1, PageRequest.MaxSize, reverse), Ct)).Items;

    private static FilterNode Filter(string json) => FilterParser.Parse(json, ResourceKind.Conclusion)!;

    // ---------------------------------------------------------------- insert and get

    [Fact]
    public async Task Insert_StoresConclusions()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var provenance = new JsonObject { ["source_message_ids"] = new JsonArray("m1", "m2"), ["prompt"] = "deriver.v1" };

        var inserted = await store.Conclusions.InsertAsync(
            workspace,
            [Fact("Bob lives in Lisbon.", session, Vector(0), internalMetadata: provenance), Fact("Bob likes tea.", null)],
            Ct);

        inserted.Count.ShouldBe(2);
        inserted.Select(c => c.PublicId).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        var first = inserted[0];
        first.WorkspaceName.ShouldBe(workspace);
        first.ObserverName.ShouldBe("alice");
        first.ObservedName.ShouldBe("bob");
        first.SessionName.ShouldBe(session);
        first.Level.ShouldBe(ConclusionLevel.Explicit);
        first.Content.ShouldBe("Bob lives in Lisbon.");
        first.TimesDerived.ShouldBe(1);
        first.SourceIds.ShouldBeEmpty();
        first.InternalMetadata.ToJsonString().ShouldBe(provenance.ToJsonString());
        first.CreatedAt.ShouldBe(Start);
        inserted[1].SessionName.ShouldBeNull();
        inserted[1].InternalMetadata.ToJsonString().ShouldBe("{}");

        var reread = (await store.Conclusions.GetAsync(workspace, first.PublicId, Ct)).ShouldNotBeNull();
        Describe(reread).ShouldBe(Describe(first));
        (await store.Conclusions.GetAsync(workspace, first.PublicId.ToUpperInvariant(), Ct)).ShouldBeNull();
        (await store.Conclusions.GetAsync(workspace, Unique("missing"), Ct)).ShouldBeNull();

        // API inserts are not derivations: the dream counters stay untouched.
        var collection = (await store.Conclusions.GetCollectionAsync(workspace, "alice", "bob", Ct)).ShouldNotBeNull();
        collection.NewExplicitCount.ShouldBe(0);
        collection.LastActivityAt.ShouldBeNull();
        collection.CreatedAt.ShouldBe(Start);
        (await store.Conclusions.GetCollectionAsync(workspace, "bob", "alice", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Insert_InvalidReference_ThrowsAndStoresNothing()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);

        await Should.ThrowAsync<NotFoundException>(() => store.Conclusions.InsertAsync(
            workspace, [Fact("ok", session), Fact("bad", session, observed: "nobody")], Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Conclusions.InsertAsync(
            workspace, [Fact("ok", session), Fact("bad", Unique("missing-session"))], Ct));
        await Should.ThrowAsync<ArgumentException>(() => store.Conclusions.InsertAsync(
            workspace, [Fact("ok", session), Fact("bad", session, new float[3])], Ct));
        await Should.ThrowAsync<NachosValidationException>(() => store.Conclusions.InsertAsync(
            workspace, [Fact("bad", session, internalMetadata: new JsonObject { ["n"] = double.NaN })], Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Conclusions.InsertAsync(Unique("missing"), [Fact("x", null)], Ct));

        (await ListAllAsync(store, workspace)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Json_InputAndOutput_AreIsolated()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var provenance = new JsonObject { ["k"] = "v" };

        var inserted = await InsertAsync(store, workspace, Fact("fact", session, internalMetadata: provenance));
        provenance["k"] = "changed";
        inserted.InternalMetadata["k"] = "changed too";

        var reread = (await store.Conclusions.GetAsync(workspace, inserted.PublicId, Ct)).ShouldNotBeNull();
        reread.InternalMetadata.ToJsonString().ShouldBe("""{"k":"v"}""");
        reread.InternalMetadata.Parent.ShouldBeNull();
    }

    // ---------------------------------------------------------------- reads

    [Fact]
    public async Task List_FiltersAndOrders()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var a = await InsertAsync(store, workspace, Fact("a", session));
        clock.Advance(TimeSpan.FromSeconds(1));
        var b = await InsertAsync(store, workspace, Fact("b", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        var c = await InsertAsync(store, workspace, Fact("c", session, observer: "bob", observed: "alice"));

        (await ListAllAsync(store, workspace)).Select(x => x.Content).ShouldBe(["a", "b", "c"]);
        (await ListAllAsync(store, workspace, reverse: true)).Select(x => x.Content).ShouldBe(["c", "b", "a"]);
        (await ListAllAsync(store, workspace, Filter("""{"observer_id":"alice"}"""))).Select(x => x.Content).ShouldBe(["a", "b"]);
        (await ListAllAsync(store, workspace, Filter("""{"observed_id":"alice"}"""))).Select(x => x.Content).ShouldBe(["c"]);
        (await ListAllAsync(store, workspace, Filter($$"""{"session_id":"{{session}}"}"""))).Select(x => x.Content).ShouldBe(["a", "c"]);
        (await ListAllAsync(store, workspace, Filter($$$"""{"session_id":{"ne":"{{{session}}}"}}"""))).Select(x => x.Content).ShouldBe(["b"]);
        (await ListAllAsync(store, workspace, Filter($$"""{"id":"{{b.PublicId}}"}"""))).ShouldHaveSingleItem().PublicId.ShouldBe(b.PublicId);

        var page = await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(2, 2), Ct);
        page.Items.ShouldHaveSingleItem().PublicId.ShouldBe(c.PublicId);
        page.Total.ShouldBe(3);
        page.Pages.ShouldBe(2);

        // Most frequently derived first: TimesDerived descending, then newest first.
        var lease = await LeaseAsync(store, workspace, session);
        await store.Conclusions.CommitDerivationAsync(lease, new DerivationChanges(workspace, [], [a.PublicId, a.PublicId], []), Ct);
        (await ListAllAsync(store, workspace, order: ConclusionOrder.TimesDerived, reverse: true))
            .Select(x => (x.Content, x.TimesDerived)).ShouldBe([("a", 3), ("c", 1), ("b", 1)]);

        await Should.ThrowAsync<NotFoundException>(() =>
            store.Conclusions.ListAsync(Unique("missing"), null, ConclusionOrder.CreatedAt, new PageRequest(), Ct));
    }

    [Fact]
    public async Task Query_RanksByCosineDistance()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await InsertAsync(store, workspace, Fact("far", session, Vector(1)));
        await InsertAsync(store, workspace, Fact("near", session, Vector(0, tilt: 0.5f)));
        await InsertAsync(store, workspace, Fact("nearest", session, Vector(0, tilt: 0.1f)));
        await InsertAsync(store, workspace, Fact("pending", session));
        await InsertAsync(store, workspace, Fact("other collection", session, Vector(0), observer: "bob", observed: "alice"));

        var all = await store.Conclusions.QueryAsync(
            workspace, Vector(0), 10, null, Filter("""{"observer_id":"alice"}"""), Ct);
        all.Select(m => m.Conclusion.Content).ShouldBe(["nearest", "near", "far"]);
        all[0].Distance.ShouldBe(TiltDistance(0.1f), 1e-4);
        all[1].Distance.ShouldBe(TiltDistance(0.5f), 1e-4);
        all[2].Distance.ShouldBe(1, 1e-4);

        (await store.Conclusions.QueryAsync(workspace, Vector(0), 1, null, null, Ct))
            .ShouldHaveSingleItem().Conclusion.Content.ShouldBe("other collection");
        (await store.Conclusions.QueryAsync(workspace, Vector(0), 10, 0.2, Filter("""{"observer_id":"alice"}"""), Ct))
            .Select(m => m.Conclusion.Content).ShouldBe(["nearest", "near"]);
        await Should.ThrowAsync<ArgumentException>(() => store.Conclusions.QueryAsync(workspace, new float[2], 10, null, null, Ct));
    }

    [Fact]
    public async Task FindExact_MatchesLevelContentAndSession()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var other = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, other, null, null, null, Ct);
        var original = await InsertAsync(store, workspace, Fact("Bob lives in Lisbon.", session));
        clock.Advance(TimeSpan.FromSeconds(1));
        await InsertAsync(store, workspace, Fact("Bob lives in Lisbon.", other));

        Task<ConclusionRecord?> Find(string content, string? restrictToSession, ConclusionLevel level = ConclusionLevel.Explicit, string observer = "alice") =>
            store.Conclusions.FindExactAsync(workspace, observer, "bob", level, content, restrictToSession, Ct);

        (await Find("Bob lives in Lisbon.", session)).ShouldNotBeNull().PublicId.ShouldBe(original.PublicId);
        (await Find("Bob lives in Lisbon.", other)).ShouldNotBeNull().SessionName.ShouldBe(other);
        (await Find("Bob lives in Lisbon.", null)).ShouldNotBeNull().PublicId.ShouldBe(original.PublicId);
        (await Find("bob lives in lisbon.", session)).ShouldBeNull();
        (await Find("Bob lives in Lisbon.", Unique("sess"))).ShouldBeNull();
        (await Find("Bob lives in Lisbon.", session, ConclusionLevel.Deductive)).ShouldBeNull();
        (await Find("Bob lives in Lisbon.", session, observer: "bob")).ShouldBeNull();
    }

    [Fact]
    public async Task FindNearest_RespectsThresholdAndSession()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var other = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, other, null, null, null, Ct);
        var inSession = await InsertAsync(store, workspace, Fact("in session", session, Vector(0, tilt: 0.2f)));
        var elsewhere = await InsertAsync(store, workspace, Fact("elsewhere", other, Vector(0, tilt: 0.1f)));
        await InsertAsync(store, workspace, Fact("pending", session));

        Task<ConclusionMatch?> Find(double maxDistance, string? restrictToSession) =>
            store.Conclusions.FindNearestAsync(workspace, "alice", "bob", ConclusionLevel.Explicit, Vector(0), maxDistance, restrictToSession, Ct);

        var any = (await Find(0.05, null)).ShouldNotBeNull();
        any.Conclusion.PublicId.ShouldBe(elsewhere.PublicId);
        any.Distance.ShouldBe(TiltDistance(0.1f), 1e-4);
        (await Find(0.05, session)).ShouldNotBeNull().Conclusion.PublicId.ShouldBe(inSession.PublicId);
        (await Find(TiltDistance(0.2f) / 2, session)).ShouldBeNull();
        (await store.Conclusions.FindNearestAsync(workspace, "bob", "alice", ConclusionLevel.Explicit, Vector(0), 2, null, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task FindNearest_FiltersByLevelBeforeChoosingTheNearest()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var deductive = await InsertAsync(
            store, workspace, Fact("deduced", session, Vector(0), level: ConclusionLevel.Deductive));
        var explicitFact = await InsertAsync(store, workspace, Fact("stated", session, Vector(0, tilt: 0.1f)));

        (await store.Conclusions.FindNearestAsync(workspace, "alice", "bob", ConclusionLevel.Explicit, Vector(0), 0.05, session, Ct))
            .ShouldNotBeNull().Conclusion.PublicId.ShouldBe(explicitFact.PublicId);
        (await store.Conclusions.FindNearestAsync(workspace, "alice", "bob", ConclusionLevel.Deductive, Vector(0), 0.05, session, Ct))
            .ShouldNotBeNull().Conclusion.PublicId.ShouldBe(deductive.PublicId);
        (await store.Conclusions.FindNearestAsync(workspace, "alice", "bob", ConclusionLevel.Inductive, Vector(0), 2, null, Ct))
            .ShouldBeNull();
    }

    // ---------------------------------------------------------------- derivation commit

    [Fact]
    public async Task CommitDerivation_AppliesChangesAndCompletesTheLease()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var corroborated = await InsertAsync(store, workspace, Fact("Bob likes tea.", session, Vector(1)));
        var replaced = await InsertAsync(store, workspace, Fact("Bob has a dog.", session, Vector(2)));
        var lease = await LeaseAsync(store, workspace, session);
        clock.Advance(TimeSpan.FromMinutes(1));

        await store.Conclusions.CommitDerivationAsync(
            lease,
            new DerivationChanges(
                workspace,
                [
                    Fact("Bob lives in Lisbon.", session, Vector(0)),
                    Fact("Bob is a peer.", session, observer: "bob"),
                    Fact("A pattern.", null, level: ConclusionLevel.Inductive),
                ],
                [corroborated.PublicId],
                [new ConclusionReplacement(replaced.PublicId, "Bob has a dog named Rex.", Vector(3))]),
            Ct);

        (await store.Conclusions.GetAsync(workspace, corroborated.PublicId, Ct)).ShouldNotBeNull().TimesDerived.ShouldBe(2);
        var replacement = (await store.Conclusions.GetAsync(workspace, replaced.PublicId, Ct)).ShouldNotBeNull();
        replacement.Content.ShouldBe("Bob has a dog named Rex.");
        replacement.TimesDerived.ShouldBe(2);
        replacement.CreatedAt.ShouldBe(replaced.CreatedAt);
        (await store.Conclusions.FindNearestAsync(workspace, "alice", "bob", ConclusionLevel.Explicit, Vector(3), 0.01, null, Ct))
            .ShouldNotBeNull().Conclusion.PublicId.ShouldBe(replaced.PublicId);

        var inserted = (await ListAllAsync(store, workspace, Filter("""{"observer_id":"alice"}""")))
            .Single(c => c.Content == "Bob lives in Lisbon.");
        inserted.TimesDerived.ShouldBe(1);
        inserted.CreatedAt.ShouldBe(Start.AddMinutes(1));

        // Dream counters: explicit inserts per collection, activity on every collection inserted into.
        var aliceOnBob = (await store.Conclusions.GetCollectionAsync(workspace, "alice", "bob", Ct)).ShouldNotBeNull();
        aliceOnBob.NewExplicitCount.ShouldBe(1);
        aliceOnBob.LastActivityAt.ShouldBe(Start.AddMinutes(1));
        aliceOnBob.LastDreamAt.ShouldBeNull();
        (await store.Conclusions.GetCollectionAsync(workspace, "bob", "bob", Ct)).ShouldNotBeNull().NewExplicitCount.ShouldBe(1);

        (await QueueAsync(store, workspace)).ShouldBe(new QueueCounts(0, 0, 1, 0));
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.CompleteAsync(lease, Ct));
    }

    [Fact]
    public async Task CommitDerivation_IsAtomicWithCompletion()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var existing = await InsertAsync(store, workspace, Fact("Bob likes tea.", session));
        var stale = await LeaseAsync(store, workspace, session, "a", TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(2));
        var policy = new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Representation] = WorkBatchPolicy.Immediate };
        var current = (await store.WorkQueue.TryAcquireAsync("b", policy, Lease, Ct)).ShouldNotBeNull();

        var changes = new DerivationChanges(
            workspace,
            [Fact("Bob lives in Lisbon.", session)],
            [existing.PublicId],
            [new ConclusionReplacement(existing.PublicId, "Bob likes green tea.", null)]);
        await Should.ThrowAsync<LeaseLostException>(() => store.Conclusions.CommitDerivationAsync(stale, changes, Ct));

        Describe((await ListAllAsync(store, workspace)).ShouldHaveSingleItem()).ShouldBe(Describe(existing));
        (await store.Conclusions.GetCollectionAsync(workspace, "alice", "bob", Ct)).ShouldNotBeNull().NewExplicitCount.ShouldBe(0);
        (await QueueAsync(store, workspace)).ShouldBe(new QueueCounts(0, 1, 0, 0));

        // The new owner redoes the batch from scratch; its single commit counts once.
        await store.Conclusions.CommitDerivationAsync(current, changes, Ct);
        (await store.Conclusions.GetAsync(workspace, existing.PublicId, Ct)).ShouldNotBeNull().TimesDerived.ShouldBe(2);
        (await QueueAsync(store, workspace)).ShouldBe(new QueueCounts(0, 0, 1, 0));
    }

    [Fact]
    public async Task CommitDerivation_CorroborationOrReplacementOnly_RecordsActivityButNoNewExplicit()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var corroborated = await InsertAsync(store, workspace, Fact("Bob likes tea.", session));
        var replaced = await InsertAsync(store, workspace, Fact("Alice has a cat.", session, observer: "bob", observed: "alice"));
        var lease = await LeaseAsync(store, workspace, session);
        clock.Advance(TimeSpan.FromMinutes(1));

        await store.Conclusions.CommitDerivationAsync(
            lease,
            new DerivationChanges(workspace, [], [corroborated.PublicId], [new ConclusionReplacement(replaced.PublicId, "Alice has a cat named Tom.", null)]),
            Ct);

        foreach (var (observer, observed) in new[] { ("alice", "bob"), ("bob", "alice") })
        {
            var collection = (await store.Conclusions.GetCollectionAsync(workspace, observer, observed, Ct)).ShouldNotBeNull();
            collection.LastActivityAt.ShouldBe(Start.AddMinutes(1));
            collection.NewExplicitCount.ShouldBe(0);
        }
    }

    [Fact]
    public async Task CommitDerivation_ReleasedLease_ThrowsLeaseLost()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var lease = await LeaseAsync(store, workspace, session);
        await store.WorkQueue.CompleteAsync(lease, Ct);

        await Should.ThrowAsync<LeaseLostException>(() => store.Conclusions.CommitDerivationAsync(
            lease, new DerivationChanges(workspace, [Fact("late", session)], [], []), Ct));
        (await ListAllAsync(store, workspace)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CommitDerivation_ExpiredLeaseNobodyReclaimed_Commits()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var lease = await LeaseAsync(store, workspace, session, duration: TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(10));

        await store.Conclusions.CommitDerivationAsync(lease, new DerivationChanges(workspace, [Fact("slow", session)], [], []), Ct);

        (await ListAllAsync(store, workspace)).ShouldHaveSingleItem().Content.ShouldBe("slow");
        (await QueueAsync(store, workspace)).Completed.ShouldBe(1);
    }

    [Fact]
    public async Task CommitDerivation_ChangesForAnotherWorkspace_ThrowAndChangeNothing()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var (otherWorkspace, otherSession) = await ArrangeAsync(store);
        var lease = await LeaseAsync(store, workspace, session);

        await Should.ThrowAsync<ArgumentException>(() => store.Conclusions.CommitDerivationAsync(
            lease, new DerivationChanges(otherWorkspace, [Fact("misrouted", otherSession)], [], []), Ct));

        (await ListAllAsync(store, otherWorkspace)).ShouldBeEmpty();
        (await QueueAsync(store, workspace)).ShouldBe(new QueueCounts(0, 1, 0, 0));
        await store.WorkQueue.CompleteAsync(lease, Ct);
    }

    [Fact]
    public async Task CommitDerivation_InvalidChange_ChangesNothingAndKeepsTheLease()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var existing = await InsertAsync(store, workspace, Fact("Bob likes tea.", session));
        var lease = await LeaseAsync(store, workspace, session);

        await Should.ThrowAsync<NotFoundException>(() => store.Conclusions.CommitDerivationAsync(
            lease,
            new DerivationChanges(workspace, [Fact("ok", session), Fact("bad", session, observer: "nobody")], [existing.PublicId], []),
            Ct));
        await Should.ThrowAsync<ArgumentException>(() => store.Conclusions.CommitDerivationAsync(
            lease,
            new DerivationChanges(workspace, [], [existing.PublicId], [new ConclusionReplacement(existing.PublicId, "x", new float[5])]),
            Ct));

        (await ListAllAsync(store, workspace)).ShouldHaveSingleItem().TimesDerived.ShouldBe(1);
        (await QueueAsync(store, workspace)).ShouldBe(new QueueCounts(0, 1, 0, 0));
        await store.WorkQueue.CompleteAsync(lease, Ct);
    }

    [Fact]
    public async Task CommitDerivation_SkipsTombstonedTargets()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var deleted = await InsertAsync(store, workspace, Fact("Bob likes tea.", session));
        await store.Conclusions.TombstoneAsync(workspace, deleted.PublicId, Ct);
        var lease = await LeaseAsync(store, workspace, session);

        await store.Conclusions.CommitDerivationAsync(
            lease,
            new DerivationChanges(
                workspace, [], [deleted.PublicId, Unique("missing")], [new ConclusionReplacement(deleted.PublicId, "x", null)]),
            Ct);

        (await store.Conclusions.GetAsync(workspace, deleted.PublicId, Ct)).ShouldBeNull();
        (await QueueAsync(store, workspace)).Completed.ShouldBe(1);
    }

    // ---------------------------------------------------------------- tombstones

    [Fact]
    public async Task Tombstoned_IsInvisibleToEveryRead()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var embedded = await InsertAsync(store, workspace, Fact("embedded", session, Vector(0)));
        var pending = await InsertAsync(store, workspace, Fact("pending", session));

        await store.Conclusions.TombstoneAsync(workspace, embedded.PublicId, Ct);
        await store.Conclusions.TombstoneAsync(workspace, pending.PublicId, Ct);
        await store.Conclusions.TombstoneAsync(workspace, pending.PublicId, Ct);
        await store.Conclusions.TombstoneAsync(workspace, Unique("missing"), Ct);

        (await store.Conclusions.GetAsync(workspace, embedded.PublicId, Ct)).ShouldBeNull();
        (await ListAllAsync(store, workspace)).ShouldBeEmpty();
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.TimesDerived, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Conclusions.QueryAsync(workspace, Vector(0), 10, null, null, Ct)).ShouldBeEmpty();
        (await store.Conclusions.FindExactAsync(workspace, "alice", "bob", ConclusionLevel.Explicit, "embedded", null, Ct)).ShouldBeNull();
        (await store.Conclusions.FindNearestAsync(workspace, "alice", "bob", ConclusionLevel.Explicit, Vector(0), 2, null, Ct)).ShouldBeNull();
        (await store.Embeddings.ListPendingAsync(1000, Ct)).ShouldNotContain(p => p.ConclusionId == pending.PublicId);
    }

    [Fact]
    public async Task PurgeTombstones_RemovesOnlyThoseOlderThanRetention()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var old = await InsertAsync(store, workspace, Fact("old", session));
        var recent = await InsertAsync(store, workspace, Fact("recent", session));
        var live = await InsertAsync(store, workspace, Fact("live", session));
        await store.Conclusions.TombstoneAsync(workspace, old.PublicId, Ct);
        clock.Advance(TimeSpan.FromHours(1));
        await store.Conclusions.TombstoneAsync(workspace, recent.PublicId, Ct);

        (await store.Conclusions.PurgeTombstonesAsync(TimeSpan.FromHours(1), Ct)).ShouldBe(1);
        (await store.Conclusions.PurgeTombstonesAsync(TimeSpan.FromHours(1), Ct)).ShouldBe(0);
        clock.Advance(TimeSpan.FromHours(1));
        (await store.Conclusions.PurgeTombstonesAsync(TimeSpan.FromHours(1), Ct)).ShouldBe(1);
        (await ListAllAsync(store, workspace)).ShouldHaveSingleItem().PublicId.ShouldBe(live.PublicId);
    }

    // ---------------------------------------------------------------- peer cards

    [Fact]
    public async Task PeerCard_SetReplacesEntries()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, _) = await ArrangeAsync(store);

        (await store.PeerCards.GetAsync(workspace, "alice", "bob", Ct)).ShouldBeNull();

        var entries = new List<string> { "Name: Bob", "Lives in Lisbon" };
        var set = await store.PeerCards.SetAsync(workspace, "alice", "bob", entries, PeerCardAuthor.Api, Ct);
        entries.Add("mutated after the call");
        set.Entries.ShouldBe(["Name: Bob", "Lives in Lisbon"]);
        set.UpdatedAt.ShouldBe(Start);
        set.UpdatedBy.ShouldBe(PeerCardAuthor.Api);

        clock.Advance(TimeSpan.FromMinutes(1));
        await store.PeerCards.SetAsync(workspace, "alice", "bob", ["Prefers tea"], PeerCardAuthor.Dream, Ct);
        var card = (await store.PeerCards.GetAsync(workspace, "alice", "bob", Ct)).ShouldNotBeNull();
        card.ShouldSatisfyAllConditions(
            () => card.WorkspaceName.ShouldBe(workspace),
            () => card.ObserverName.ShouldBe("alice"),
            () => card.ObservedName.ShouldBe("bob"),
            () => card.Entries.ShouldBe(["Prefers tea"]),
            () => card.UpdatedAt.ShouldBe(Start.AddMinutes(1)),
            () => card.UpdatedBy.ShouldBe(PeerCardAuthor.Dream));
        (await store.PeerCards.GetAsync(workspace, "bob", "alice", Ct)).ShouldBeNull();

        await store.PeerCards.SetAsync(workspace, "bob", "bob", [], PeerCardAuthor.Api, Ct);
        (await store.PeerCards.GetAsync(workspace, "bob", "bob", Ct)).ShouldNotBeNull().Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task PeerCard_UnknownPeerOrWorkspace_ThrowsNotFound()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, _) = await ArrangeAsync(store);

        await Should.ThrowAsync<NotFoundException>(() => store.PeerCards.SetAsync(workspace, "alice", "nobody", ["x"], PeerCardAuthor.Api, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.PeerCards.SetAsync(workspace, "nobody", "bob", ["x"], PeerCardAuthor.Api, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.PeerCards.GetAsync(Unique("missing"), "alice", "bob", Ct));
        (await store.PeerCards.GetAsync(workspace, "alice", "nobody", Ct)).ShouldBeNull();
    }

    // ---------------------------------------------------------------- message range (deriver context)

    [Fact]
    public async Task Messages_ListRange_ReturnsTheInclusiveSeqRangeInOrder()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var appended = await store.Messages.AppendAsync(
            workspace,
            session,
            [.. Enumerable.Range(1, 5).Select(i => new NewMessage(i % 2 == 0 ? "alice" : "bob", $"m{i}", 1, null, null))],
            null,
            Ct);

        var range = await store.Messages.ListRangeAsync(workspace, session, 2, 4, Ct);

        range.Select(m => m.PublicId).ShouldBe(appended.Skip(1).Take(3).Select(m => m.PublicId));
        range.Select(m => m.Seq).ShouldBe([2L, 3L, 4L]);
        (await store.Messages.ListRangeAsync(workspace, session, -10, 1, Ct)).Single().Content.ShouldBe("m1");
        (await store.Messages.ListRangeAsync(workspace, session, 4, 2, Ct)).ShouldBeEmpty();
        (await store.Messages.ListRangeAsync(workspace, session, 6, 100, Ct)).ShouldBeEmpty();
        await Should.ThrowAsync<NotFoundException>(() => store.Messages.ListRangeAsync(workspace, "missing", 1, 5, Ct));
    }

    // ---------------------------------------------------------------- embedding backlog

    [Fact]
    public async Task Embeddings_PendingBecomesQueryableOnceSaved()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var first = await InsertAsync(store, workspace, Fact("first", session));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await InsertAsync(store, workspace, Fact("second", session));
        await InsertAsync(store, workspace, Fact("embedded", session, Vector(5)));

        var pending = (await store.Embeddings.ListPendingAsync(1000, Ct)).Where(p => p.WorkspaceName == workspace).ToList();
        pending.ShouldBe([new PendingEmbedding(workspace, first.PublicId, "first"), new PendingEmbedding(workspace, second.PublicId, "second")]);
        (await store.Embeddings.ListPendingAsync(1, Ct)).Count.ShouldBe(1);

        await Should.ThrowAsync<ArgumentException>(() => store.Embeddings.SaveAsync(pending[0], new float[4], Ct));
        await store.Embeddings.SaveAsync(pending[0], Vector(0), Ct);

        (await store.Embeddings.ListPendingAsync(1000, Ct)).Select(p => p.ConclusionId).ShouldNotContain(first.PublicId);
        (await store.Conclusions.QueryAsync(workspace, Vector(0), 1, null, null, Ct))
            .ShouldHaveSingleItem().Conclusion.PublicId.ShouldBe(first.PublicId);
    }

    [Fact]
    public async Task Embeddings_StaleSave_IsIgnored()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var conclusion = await InsertAsync(store, workspace, Fact("Bob has a dog.", session));
        var claimed = (await store.Embeddings.ListPendingAsync(1000, Ct)).Single(p => p.ConclusionId == conclusion.PublicId);

        // Replaced while the reconciler was embedding the old content.
        var lease = await LeaseAsync(store, workspace, session);
        await store.Conclusions.CommitDerivationAsync(
            lease,
            new DerivationChanges(workspace, [], [], [new ConclusionReplacement(conclusion.PublicId, "Bob has a dog named Rex.", null)]),
            Ct);
        await store.Embeddings.SaveAsync(claimed, Vector(0), Ct);
        await store.Embeddings.MarkFailedAsync(claimed, Ct);

        (await store.Conclusions.QueryAsync(workspace, Vector(0), 10, null, null, Ct)).ShouldBeEmpty();
        (await store.Embeddings.ListPendingAsync(1000, Ct)).ShouldContain(
            new PendingEmbedding(workspace, conclusion.PublicId, "Bob has a dog named Rex."));
    }

    [Fact]
    public async Task Embeddings_MarkFailed_LeavesTheBacklog()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var conclusion = await InsertAsync(store, workspace, Fact("unembeddable", session));
        var claimed = (await store.Embeddings.ListPendingAsync(1000, Ct)).Single(p => p.ConclusionId == conclusion.PublicId);

        await store.Embeddings.MarkFailedAsync(claimed, Ct);
        await store.Embeddings.SaveAsync(claimed, Vector(0), Ct);

        (await store.Embeddings.ListPendingAsync(1000, Ct)).ShouldNotContain(p => p.ConclusionId == conclusion.PublicId);
        (await store.Conclusions.QueryAsync(workspace, Vector(0), 10, null, null, Ct)).ShouldBeEmpty();
        (await store.Conclusions.GetAsync(workspace, conclusion.PublicId, Ct)).ShouldNotBeNull();
    }
}
