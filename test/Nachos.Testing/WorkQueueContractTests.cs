using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// Behavioural contract of <see cref="IWorkQueue"/> and of the transactional enqueue in
/// <see cref="IMessageStore.AppendAsync(string, string, IReadOnlyList{NewMessage}, IdempotencyWrite?, IReadOnlyList{DeriveIntent}?, CancellationToken)"/>.
/// A provider's test project derives from this class and supplies the store.
/// </summary>
/// <remarks>
/// The queue spans workspaces, and every test claims whatever is due, so each test must start from an empty queue. A
/// provider whose stores share one database overrides <see cref="ResetQueueAsync"/> to empty it, and every derived class
/// must join the non-parallel <see cref="M2StoreContracts"/>.
/// </remarks>
public abstract class WorkQueueContractTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> Representation =
        new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Representation] = WorkBatchPolicy.Immediate };

    private readonly ConcurrentBag<IMemoryStore> _stores = [];

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Creates an empty store whose time-based values come from <paramref name="clock"/>.</summary>
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

    private static SessionPeerConfig Observing() => new(ObserveMe: null, ObserveOthers: true);

    /// <summary>A workspace and a session whose members have the given configs.</summary>
    private static async Task<(string Workspace, string Session)> ArrangeAsync(
        IMemoryStore store, Dictionary<string, SessionPeerConfig>? members = null)
    {
        var workspace = Unique("ws");
        var session = Unique("sess");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, members, Ct);
        return (workspace, session);
    }

    private static Task<IReadOnlyList<MessageRecord>> AppendAsync(
        IMemoryStore store,
        string workspace,
        string session,
        string sender,
        int tokens = 10,
        DeriveIntent? intent = null,
        DateTimeOffset? createdAt = null,
        IdempotencyWrite? idempotency = null) =>
        store.Messages.AppendAsync(
            workspace, session, [new NewMessage(sender, $"message from {sender}", tokens, null, createdAt)], idempotency, [intent ?? DeriveIntent.Derive], Ct);

    private static IdempotencyWrite Write(string key, Func<IReadOnlyList<MessageRecord>, string>? serialize = null) =>
        new(key, new string('a', 64), 201, serialize ?? (messages => messages[0].PublicId), TimeSpan.FromMinutes(10));

    private static Task<WorkLease?> AcquireAsync(IMemoryStore store, string owner = "worker-1") =>
        store.WorkQueue.TryAcquireAsync(owner, Representation, Lease, Ct);

    private static Dictionary<WorkTaskType, WorkBatchPolicy> Policy(int eligibilityTokens, TimeSpan maxAge, int batchTokenTarget) =>
        new Dictionary<WorkTaskType, WorkBatchPolicy>
        {
            [WorkTaskType.Representation] = new(eligibilityTokens, maxAge, batchTokenTarget),
        };

    private static async Task<QueueCounts> TotalsAsync(
        IMemoryStore store, string workspace, string? observer = null, string? sender = null, string? session = null) =>
        (await store.WorkQueue.GetStatusAsync(workspace, observer, sender, session, Ct)).Totals;

    // ---------------------------------------------------------------- enqueue

    [Fact]
    public async Task Append_EnqueuesRepresentationWork_Atomically()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store, new() { ["alice"] = Observing(), ["bob"] = new() });

        var message = (await AppendAsync(store, workspace, session, "bob", tokens: 17)).Single();

        var lease = (await AcquireAsync(store)).ShouldNotBeNull();
        lease.WorkUnitKey.ShouldBe(WorkUnitKeys.Representation(workspace, session, "bob"));
        lease.Owner.ShouldBe("worker-1");
        var item = lease.Items.ShouldHaveSingleItem();
        item.TaskType.ShouldBe(WorkTaskType.Representation);
        item.WorkUnitKey.ShouldBe(lease.WorkUnitKey);
        item.WorkspaceName.ShouldBe(workspace);
        item.SessionName.ShouldBe(session);
        item.MessageId.ShouldBe(message.PublicId);
        item.TokenCount.ShouldBe(17);
        item.Attempts.ShouldBe(1);
        item.CreatedAt.ShouldBe(Start);
        item.Payload.ToJsonString().ShouldBe("""{"observers":["alice","bob"]}""");
        await store.WorkQueue.CompleteAsync(lease, Ct);

        // A serializer throw rolls the work back with the messages.
        await Should.ThrowAsync<InvalidOperationException>(() => AppendAsync(
            store, workspace, session, "bob", idempotency: Write("k1", _ => throw new InvalidOperationException("boom"))));
        (await AcquireAsync(store)).ShouldBeNull();

        // An idempotency duplicate enqueues nothing either.
        await AppendAsync(store, workspace, session, "bob", idempotency: Write("k2"));
        await Should.ThrowAsync<IdempotencyDuplicateException>(() =>
            AppendAsync(store, workspace, session, "bob", idempotency: Write("k2")));
        (await AcquireAsync(store)).ShouldNotBeNull().Items.Count.ShouldBe(1);
        (await TotalsAsync(store, workspace)).Total.ShouldBe(2);
    }

    [Fact]
    public async Task Append_NoObservers_EnqueuesNothing()
    {
        var (store, _) = await NewStoreAsync();

        // Session-level observe_me false silences the sender, even with an observing member.
        var (workspace, session) = await ArrangeAsync(
            store, new() { ["alice"] = Observing(), ["bob"] = new(ObserveMe: false) });
        await AppendAsync(store, workspace, session, "bob");

        // Peer-level observe_me false does too.
        var (workspace2, session2) = await ArrangeAsync(store, new() { ["alice"] = Observing() });
        await store.Peers.GetOrCreateAsync(workspace2, "carol", null, new JsonObject { ["observe_me"] = false }, Ct);
        await AppendAsync(store, workspace2, session2, "carol");

        (await AcquireAsync(store)).ShouldBeNull();
        (await TotalsAsync(store, workspace)).Total.ShouldBe(0);
        (await TotalsAsync(store, workspace2)).Total.ShouldBe(0);
    }

    [Fact]
    public async Task Append_DeriveFalse_EnqueuesNothing()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store, new() { ["alice"] = Observing() });

        await store.Messages.AppendAsync(workspace, session, [new NewMessage("bob", "no work", 5, null, null)], null, Ct);
        await store.Messages.AppendAsync(workspace, session, [new NewMessage("bob", "no work", 5, null, null)], null, null, Ct);
        await AppendAsync(store, workspace, session, "bob", intent: DeriveIntent.Skip);
        (await AcquireAsync(store)).ShouldBeNull();

        var batch = await store.Messages.AppendAsync(
            workspace,
            session,
            [new NewMessage("bob", "skip", 5, null, null), new NewMessage("bob", "derive", 5, null, null)],
            null,
            [DeriveIntent.Skip, DeriveIntent.Derive],
            Ct);
        var lease = (await AcquireAsync(store)).ShouldNotBeNull();
        lease.Items.ShouldHaveSingleItem().MessageId.ShouldBe(batch[1].PublicId);
    }

    [Fact]
    public async Task Append_SnapshotsTheMessageReasoningInThePayload()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);

        await store.Messages.AppendAsync(
            workspace,
            session,
            [new NewMessage("bob", "a", 1, null, null), new NewMessage("bob", "b", 1, null, null)],
            null,
            [new DeriveIntent(true, ReasoningEnabled: true, CustomInstructions: "Note hobbies."), DeriveIntent.Derive],
            Ct);

        var lease = (await store.WorkQueue.TryAcquireAsync("w", Policy(0, TimeSpan.Zero, 1000), Lease, Ct)).ShouldNotBeNull();
        lease.Items.Select(item => item.Payload.ToJsonString()).ShouldBe(
        [
            """{"observers":["bob"],"reasoning_enabled":true,"custom_instructions":"Note hobbies."}""",
            """{"observers":["bob"]}""",
        ]);
    }

    [Fact]
    public async Task Append_IntentCountMismatch_ThrowsAndStoresNothing()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);

        await Should.ThrowAsync<ArgumentException>(() => store.Messages.AppendAsync(
            workspace, session, [new NewMessage("bob", "a", 1, null, null)], null, [], Ct));

        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await AcquireAsync(store)).ShouldBeNull();
    }

    [Fact]
    public async Task Append_ResolvesObserversAtMessageTime_AndSnapshotsThem()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store, new() { ["alice"] = Observing(), ["carol"] = Observing() });
        clock.Advance(TimeSpan.FromHours(1));
        await store.Sessions.RemovePeersAsync(workspace, session, ["carol"], Ct);
        clock.Advance(TimeSpan.FromHours(1));

        // Sent now, after carol left; then backdated to while she was still a member.
        await AppendAsync(store, workspace, session, "bob");
        await AppendAsync(store, workspace, session, "bob", createdAt: Start.AddMinutes(30));

        // Later configuration changes do not touch the snapshot.
        await store.Sessions.SetPeerConfigAsync(workspace, session, "alice", new SessionPeerConfig(ObserveOthers: false), Ct);

        var lease = (await store.WorkQueue.TryAcquireAsync("w", Policy(0, TimeSpan.Zero, 1000), Lease, Ct)).ShouldNotBeNull();
        lease.Items.Select(item => RepresentationPayload.Read(item.Payload).Observers).ShouldBe(
            [["alice", "bob"], ["alice", "bob", "carol"]]);
    }

    [Fact]
    public async Task Append_TwoSenders_EnqueuesTwoUnitsInOneTransaction()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        NewMessage[] batch = [new("bob", "hi", 5, null, null), new("carol", "hello", 5, null, null)];

        await Should.ThrowAsync<InvalidOperationException>(() => store.Messages.AppendAsync(
            workspace, session, batch, Write("k", _ => throw new InvalidOperationException("boom")), [DeriveIntent.Derive, DeriveIntent.Derive], Ct));
        (await TotalsAsync(store, workspace)).Total.ShouldBe(0);

        await store.Messages.AppendAsync(workspace, session, batch, null, [DeriveIntent.Derive, DeriveIntent.Derive], Ct);
        var first = (await AcquireAsync(store)).ShouldNotBeNull();
        var second = (await AcquireAsync(store)).ShouldNotBeNull();
        first.WorkUnitKey.ShouldBe(WorkUnitKeys.Representation(workspace, session, "bob"));
        second.WorkUnitKey.ShouldBe(WorkUnitKeys.Representation(workspace, session, "carol"));
        (await AcquireAsync(store)).ShouldBeNull();
    }

    // ---------------------------------------------------------------- acquire

    [Fact]
    public async Task Acquire_RespectsEligibilityAndBatchTarget()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var byAgePolicy = Policy(eligibilityTokens: 100, maxAge: TimeSpan.FromMinutes(30), batchTokenTarget: 50);
        var byTokensPolicy = Policy(eligibilityTokens: 100, maxAge: TimeSpan.FromDays(1), batchTokenTarget: 50);

        // 60 tokens, young: not due.
        await AppendAsync(store, workspace, session, "bob", tokens: 20);
        await AppendAsync(store, workspace, session, "bob", tokens: 20);
        await AppendAsync(store, workspace, session, "bob", tokens: 20);
        clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
        (await store.WorkQueue.TryAcquireAsync("w", byAgePolicy, Lease, Ct)).ShouldBeNull();

        // Old enough: due, and the batch takes items in Id order while they fit the target.
        clock.Advance(TimeSpan.FromTicks(1));
        var byAge = (await store.WorkQueue.TryAcquireAsync("w", byAgePolicy, Lease, Ct)).ShouldNotBeNull();
        byAge.Items.Select(item => item.TokenCount).ShouldBe([20, 20]);
        byAge.Items.Select(item => item.Id).ShouldBeInOrder();
        await store.WorkQueue.CompleteAsync(byAge, Ct);

        // Enough tokens: due at once. A first item larger than the target is still taken, alone.
        await AppendAsync(store, workspace, session, "dan", tokens: 80);
        await AppendAsync(store, workspace, session, "dan", tokens: 20);
        var byTokens = (await store.WorkQueue.TryAcquireAsync("w", byTokensPolicy, Lease, Ct)).ShouldNotBeNull();
        byTokens.WorkUnitKey.ShouldBe(WorkUnitKeys.Representation(workspace, session, "dan"));
        byTokens.Items.Select(item => item.TokenCount).ShouldBe([80]);
        await store.WorkQueue.CompleteAsync(byTokens, Ct);

        // What is left of each unit (20 tokens each, younger than a day) is below both thresholds.
        (await store.WorkQueue.TryAcquireAsync("w", byTokensPolicy, Lease, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Acquire_TakesOnlyRequestedTaskTypes()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var summaries = new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Summary] = WorkBatchPolicy.Immediate };
        (await store.WorkQueue.TryAcquireAsync("w", summaries, Lease, Ct)).ShouldBeNull();
        (await store.WorkQueue.TryAcquireAsync("w", new Dictionary<WorkTaskType, WorkBatchPolicy>(), Lease, Ct)).ShouldBeNull();
        (await AcquireAsync(store)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Acquire_OldestUnitFirst()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "first");
        clock.Advance(TimeSpan.FromSeconds(1));
        await AppendAsync(store, workspace, session, "second");

        (await AcquireAsync(store)).ShouldNotBeNull().WorkUnitKey
            .ShouldBe(WorkUnitKeys.Representation(workspace, session, "first"));
        (await AcquireAsync(store)).ShouldNotBeNull().WorkUnitKey
            .ShouldBe(WorkUnitKeys.Representation(workspace, session, "second"));
    }

    [Fact]
    public async Task Acquire_IsSerialPerUnit()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var batching = Policy(0, TimeSpan.Zero, 1000);
        await AppendAsync(store, workspace, session, "bob");
        await AppendAsync(store, workspace, session, "bob");

        var first = (await store.WorkQueue.TryAcquireAsync("a", batching, Lease, Ct)).ShouldNotBeNull();
        first.Items.Count.ShouldBe(2);

        // Work arriving for a held unit waits for it; other units stay available.
        await AppendAsync(store, workspace, session, "bob");
        await AppendAsync(store, workspace, session, "carol");
        var other = (await store.WorkQueue.TryAcquireAsync("b", batching, Lease, Ct)).ShouldNotBeNull();
        other.WorkUnitKey.ShouldBe(WorkUnitKeys.Representation(workspace, session, "carol"));
        (await store.WorkQueue.TryAcquireAsync("b", batching, Lease, Ct)).ShouldBeNull();

        await store.WorkQueue.CompleteAsync(first, Ct);
        var next = (await store.WorkQueue.TryAcquireAsync("b", batching, Lease, Ct)).ShouldNotBeNull();
        next.WorkUnitKey.ShouldBe(first.WorkUnitKey);
        next.Items.ShouldHaveSingleItem().Id.ShouldBeGreaterThan(first.Items.Max(item => item.Id));
        next.FencingToken.ShouldBeGreaterThan(first.FencingToken);
    }

    [Fact]
    public async Task Acquire_Concurrent_OneOwnerPerUnit()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");
        await AppendAsync(store, workspace, session, "carol");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = Enumerable.Range(0, 16)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await AcquireAsync(store, $"worker-{i}");
            }))
            .ToArray();
        gate.SetResult();

        var won = (await Task.WhenAll(claims)).OfType<WorkLease>().Select(lease => lease.WorkUnitKey);
        string[] units = [WorkUnitKeys.Representation(workspace, session, "bob"), WorkUnitKeys.Representation(workspace, session, "carol")];
        won.Order(StringComparer.Ordinal).ShouldBe(units.Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- leases and fencing

    [Fact]
    public async Task ExpiredLease_IsReclaimedWithNewFencingToken()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var first = (await store.WorkQueue.TryAcquireAsync("a", Representation, TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();

        // Expired means strictly past ExpiresAt.
        clock.Advance(TimeSpan.FromMinutes(1));
        (await AcquireAsync(store, "b")).ShouldBeNull();

        clock.Advance(TimeSpan.FromTicks(1));
        var second = (await AcquireAsync(store, "b")).ShouldNotBeNull();
        second.WorkUnitKey.ShouldBe(first.WorkUnitKey);
        second.Owner.ShouldBe("b");
        second.FencingToken.ShouldBeGreaterThan(first.FencingToken);
        second.Items.ShouldHaveSingleItem().Id.ShouldBe(first.Items.Single().Id);
        second.Items.Single().Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task ExpiredLease_NobodyReclaimed_IsStillHeld()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var lease = (await store.WorkQueue.TryAcquireAsync("a", Representation, TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromMinutes(10));

        await store.WorkQueue.RenewAsync(lease, Lease, Ct);
        await store.WorkQueue.CompleteAsync(lease, Ct);
        (await TotalsAsync(store, workspace)).Completed.ShouldBe(1);
    }

    [Fact]
    public async Task StaleFencingToken_WriteRejected()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var stale = (await store.WorkQueue.TryAcquireAsync("a", Representation, TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromMinutes(2));
        var current = (await AcquireAsync(store, "b")).ShouldNotBeNull();

        (await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(stale, Lease, Ct)))
            .WorkUnitKey.ShouldBe(stale.WorkUnitKey);
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.CompleteAsync(stale, Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.FailAsync(stale, "late", TimeSpan.Zero, 5, Ct));

        // The same owner with an old token is just as stale.
        var forged = current with { FencingToken = stale.FencingToken };
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.CompleteAsync(forged, Ct));

        (await TotalsAsync(store, workspace)).InProgress.ShouldBe(1);
        await store.WorkQueue.CompleteAsync(current, Ct);
        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(0, 0, 1, 0));
    }

    [Fact]
    public async Task ReleasedLease_RejectsFurtherWrites()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");
        await AppendAsync(store, workspace, session, "carol");

        var completed = (await AcquireAsync(store)).ShouldNotBeNull();
        await store.WorkQueue.CompleteAsync(completed, Ct);
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.CompleteAsync(completed, Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(completed, Lease, Ct));

        var failed = (await AcquireAsync(store)).ShouldNotBeNull();
        await store.WorkQueue.FailAsync(failed, "boom", TimeSpan.FromMinutes(1), 5, Ct);
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.FailAsync(failed, "boom", TimeSpan.Zero, 5, Ct));
    }

    [Fact]
    public async Task Release_ReturnsItemsWithoutConsumingAnAttempt()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var first = (await AcquireAsync(store, "a")).ShouldNotBeNull();
        await store.WorkQueue.ReleaseAsync(first, Ct);
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.ReleaseAsync(first, Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.CompleteAsync(first, Ct));
        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(1, 0, 0, 0));

        // Claimable again at once, by anyone, still on its first attempt, under a newer token.
        var second = (await AcquireAsync(store, "b")).ShouldNotBeNull();
        second.Items.ShouldHaveSingleItem().Id.ShouldBe(first.Items.Single().Id);
        second.Items.Single().Attempts.ShouldBe(1);
        second.FencingToken.ShouldBeGreaterThan(first.FencingToken);
    }

    [Fact]
    public async Task Release_OnTheLastAttempt_DoesNotDeadLetter()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");
        await store.WorkQueue.FailAsync((await AcquireAsync(store)).ShouldNotBeNull(), "first", TimeSpan.Zero, 2, Ct);

        // The second attempt is the last, but releasing it gives the attempt back: no dead letter, no backoff.
        var interrupted = (await AcquireAsync(store)).ShouldNotBeNull();
        interrupted.Items.Single().Attempts.ShouldBe(2);
        await store.WorkQueue.ReleaseAsync(interrupted, Ct);
        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(1, 0, 0, 0));

        var retried = (await AcquireAsync(store, "worker-2")).ShouldNotBeNull();
        retried.Items.Single().Attempts.ShouldBe(2);
        await store.WorkQueue.FailAsync(retried, "second", TimeSpan.Zero, 2, Ct);
        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(0, 0, 0, 1));
    }

    [Fact]
    public async Task Renew_ExtendsLease()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var lease = (await store.WorkQueue.TryAcquireAsync("a", Representation, TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromSeconds(50));
        await store.WorkQueue.RenewAsync(lease, TimeSpan.FromMinutes(1), Ct);
        clock.Advance(TimeSpan.FromSeconds(60));

        (await AcquireAsync(store, "b")).ShouldBeNull();
        clock.Advance(TimeSpan.FromTicks(1));
        (await AcquireAsync(store, "b")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Fail_BacksOffThenDeadLetters()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var first = (await AcquireAsync(store)).ShouldNotBeNull();
        await store.WorkQueue.FailAsync(first, new string('x', 5000), TimeSpan.FromMinutes(1), maxAttempts: 2, Ct);
        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(1, 0, 0, 0));

        clock.Advance(TimeSpan.FromSeconds(59));
        (await AcquireAsync(store)).ShouldBeNull();
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = (await AcquireAsync(store)).ShouldNotBeNull();
        second.Items.Single().Attempts.ShouldBe(2);

        await store.WorkQueue.FailAsync(second, "again", TimeSpan.FromMinutes(1), maxAttempts: 2, Ct);
        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(0, 0, 0, 1));
        clock.Advance(TimeSpan.FromDays(1));
        (await AcquireAsync(store)).ShouldBeNull();

        // A dead letter does not block later work of its unit.
        await AppendAsync(store, workspace, session, "bob");
        (await AcquireAsync(store)).ShouldNotBeNull().Items.ShouldHaveSingleItem().Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Fail_BackingOffItem_HoldsBackLaterItemsOfItsUnit()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var lease = (await AcquireAsync(store)).ShouldNotBeNull();
        await store.WorkQueue.FailAsync(lease, "boom", TimeSpan.FromMinutes(1), 5, Ct);
        await AppendAsync(store, workspace, session, "bob");

        (await AcquireAsync(store)).ShouldBeNull();
        clock.Advance(TimeSpan.FromMinutes(1));
        (await AcquireAsync(store)).ShouldNotBeNull().Items[0].Id.ShouldBe(lease.Items[0].Id);
    }

    [Fact]
    public async Task Fail_MixedAttempts_DeadLettersOnlyItemsAtTheLimit()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        var batching = Policy(0, TimeSpan.Zero, 1000);
        await AppendAsync(store, workspace, session, "bob");

        await store.WorkQueue.FailAsync((await AcquireAsync(store)).ShouldNotBeNull(), "first", TimeSpan.Zero, 2, Ct);
        await AppendAsync(store, workspace, session, "bob");
        var mixed = (await store.WorkQueue.TryAcquireAsync("w", batching, Lease, Ct)).ShouldNotBeNull();
        mixed.Items.Select(item => item.Attempts).ShouldBe([2, 1]);

        await store.WorkQueue.FailAsync(mixed, "second", TimeSpan.Zero, 2, Ct);
        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(Pending: 1, InProgress: 0, Completed: 0, DeadLettered: 1));
        var retry = (await store.WorkQueue.TryAcquireAsync("w", batching, Lease, Ct)).ShouldNotBeNull();
        retry.Items.ShouldHaveSingleItem().Id.ShouldBe(mixed.Items[1].Id);
    }

    [Fact]
    public async Task OverlongOwnerOrKey_IsRejected()
    {
        var (store, _) = await NewStoreAsync();
        var owner = new string('o', WorkLease.MaxOwnerLength);
        var key = new string('k', WorkUnitKeys.MaxLength);

        (await store.WorkQueue.TryLeaseAsync(key, owner, Lease, Ct)).ShouldNotBeNull();
        (await store.WorkQueue.TryAcquireAsync(owner, Representation, Lease, Ct)).ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() => store.WorkQueue.TryAcquireAsync(owner + "o", Representation, Lease, Ct));
        await Should.ThrowAsync<ArgumentException>(() => store.WorkQueue.TryLeaseAsync(Unique("k"), owner + "o", Lease, Ct));
        await Should.ThrowAsync<ArgumentException>(() => store.WorkQueue.TryLeaseAsync(key + "k", "w", Lease, Ct));
    }

    [Fact]
    public async Task TryLease_IsExclusiveAndFenced()
    {
        var (store, clock) = await NewStoreAsync();
        var key = WorkUnitKeys.Reconciler;

        var first = (await store.WorkQueue.TryLeaseAsync(key, "a", TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();
        first.Items.ShouldBeEmpty();
        first.WorkUnitKey.ShouldBe(key);
        (await store.WorkQueue.TryLeaseAsync(key, "b", TimeSpan.FromMinutes(1), Ct)).ShouldBeNull();

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1));
        var second = (await store.WorkQueue.TryLeaseAsync(key, "b", TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();
        second.FencingToken.ShouldBeGreaterThan(first.FencingToken);
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(first, Lease, Ct));

        await store.WorkQueue.CompleteAsync(second, Ct);
        (await store.WorkQueue.TryLeaseAsync(key, "a", TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull()
            .FencingToken.ShouldBeGreaterThan(second.FencingToken);
    }

    // ---------------------------------------------------------------- status and pruning

    [Fact]
    public async Task QueueStatus_CountsUnitsBySessionAndObserver()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store, new() { ["alice"] = Observing() });
        var other = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, other, null, null, null, Ct);

        await AppendAsync(store, workspace, session, "bob");
        await AppendAsync(store, workspace, session, "bob");
        await AppendAsync(store, workspace, session, "carol");
        await AppendAsync(store, workspace, other, "bob");

        var bobsLease = (await store.WorkQueue.TryAcquireAsync("w", Policy(0, TimeSpan.Zero, 1000), Lease, Ct)).ShouldNotBeNull();
        bobsLease.WorkUnitKey.ShouldBe(WorkUnitKeys.Representation(workspace, session, "bob"));
        var carolsLease = (await AcquireAsync(store)).ShouldNotBeNull();
        await store.WorkQueue.CompleteAsync(carolsLease, Ct);

        var status = await store.WorkQueue.GetStatusAsync(workspace, null, null, null, Ct);
        status.Totals.ShouldBe(new QueueCounts(Pending: 1, InProgress: 2, Completed: 1, DeadLettered: 0));
        status.Totals.Total.ShouldBe(4);
        status.Sessions.Keys.Order(StringComparer.Ordinal).ShouldBe(new[] { session, other }.Order(StringComparer.Ordinal));
        status.Sessions[session].ShouldBe(new QueueCounts(0, 2, 1, 0));
        status.Sessions[other].ShouldBe(new QueueCounts(1, 0, 0, 0));

        // alice observes everyone in the first session only; each peer observes itself.
        (await TotalsAsync(store, workspace, observer: "alice")).ShouldBe(new QueueCounts(0, 2, 1, 0));
        (await TotalsAsync(store, workspace, observer: "bob")).ShouldBe(new QueueCounts(1, 2, 0, 0));
        (await TotalsAsync(store, workspace, sender: "carol")).ShouldBe(new QueueCounts(0, 0, 1, 0));
        (await TotalsAsync(store, workspace, sender: "bob", session: other)).ShouldBe(new QueueCounts(1, 0, 0, 0));

        var filtered = await store.WorkQueue.GetStatusAsync(workspace, null, null, other, Ct);
        filtered.Sessions.Keys.ShouldBe([other]);

        var (emptyWorkspace, _) = await ArrangeAsync(store);
        var empty = await store.WorkQueue.GetStatusAsync(emptyWorkspace, null, null, null, Ct);
        empty.Totals.ShouldBe(QueueCounts.Empty);
        empty.Sessions.ShouldBeEmpty();
        await Should.ThrowAsync<NotFoundException>(() => store.WorkQueue.GetStatusAsync(Unique("missing"), null, null, null, Ct));
    }

    [Fact]
    public async Task PruneSucceeded_RemovesOnlyOldCompletedItems()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");
        await AppendAsync(store, workspace, session, "carol");
        await AppendAsync(store, workspace, session, "dan");

        await store.WorkQueue.CompleteAsync((await AcquireAsync(store)).ShouldNotBeNull(), Ct);
        clock.Advance(TimeSpan.FromHours(1));
        await store.WorkQueue.CompleteAsync((await AcquireAsync(store)).ShouldNotBeNull(), Ct);

        (await store.WorkQueue.PruneSucceededAsync(TimeSpan.FromHours(1), Ct)).ShouldBe(1);
        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(1, 0, 1, 0));
        (await store.WorkQueue.PruneSucceededAsync(TimeSpan.FromHours(1), Ct)).ShouldBe(0);
    }
}
