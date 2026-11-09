using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// The deletion job's store steps (spec §13.1, "The deletion job"): <see cref="IDeletionStore.CancelOtherWorkAsync"/>,
/// <see cref="IDeletionStore.PurgeBatchAsync"/> and <see cref="IDeletionStore.RemoveAsync"/>, with their fencing. A
/// provider's test project derives from this class and supplies the store.
/// </summary>
public abstract class DeletionPurgeContractTests : M3ContractTestBase
{
    private static NewConclusion Conclusion(
        string content, string? session = null, ConclusionLevel level = ConclusionLevel.Explicit, IReadOnlyList<string>? sources = null) =>
        new("alice", "bob", session, level, content, null, sources ?? [], null);

    /// <summary>A session with messages, chunks, a summary, memberships and an explicit conclusion.</summary>
    private static async Task<(string Workspace, string Session)> PopulatedSessionAsync(IMemoryStore store, int messages = 10)
    {
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice"), Ct);
        var appended = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend(
                [.. Enumerable.Range(0, messages).Select(i => Msg("bob", $"message {i}"))],
                Chunks: [.. Enumerable.Range(0, messages).Select(i => (IReadOnlyList<string>)[$"m{i} part 0", $"m{i} part 1"])],
                Summary: new SummaryIntent(messages, 1000)),
            Ct);
        var summaryLease = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();
        await store.Summaries.SaveAsync(
            summaryLease, workspace, session, [new NewSummary(SummaryKind.Short, "summary", appended[^1].PublicId, messages, 1)], Ct);
        await store.Conclusions.InsertAsync(workspace, [Conclusion("bob likes tea", session)], Ct);
        return (workspace, session);
    }

    /// <summary>Runs a whole deletion job under one lease.</summary>
    private static async Task RunJobAsync(IMemoryStore store, WorkLease lease, DeletionTarget target, int batchSize = 500)
    {
        await store.Deletion.CancelOtherWorkAsync(lease, target, Ct);
        while (await store.Deletion.PurgeBatchAsync(lease, target, batchSize, Ct))
        {
        }

        await store.Deletion.RemoveAsync(lease, target, Ct);
    }

    private static async Task<long> MessageCountAsync(IMemoryStore store, string workspace, string session) =>
        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total;

    private static async Task<long> PendingEmbeddingsAsync(IMemoryStore store, string workspace) =>
        (await store.Embeddings.ListPendingAsync(100_000, Ct)).Count(p => p.WorkspaceName == workspace);

    [Fact]
    public async Task Purge_IsResumableFromCheckpoint()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await PopulatedSessionAsync(store);
        var target = DeletionTarget.Session(workspace, session);
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var first = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        await store.Deletion.CancelOtherWorkAsync(first, target, Ct);

        // Messages go last, so once one is gone every earlier step has been purged.
        for (var batch = 0; await MessageCountAsync(store, workspace, session) == 10; batch++)
        {
            batch.ShouldBeLessThan(20);
            (await store.Deletion.PurgeBatchAsync(first, target, 3, Ct)).ShouldBeTrue();
        }

        (await MessageCountAsync(store, workspace, session)).ShouldBeGreaterThan(0);
        (await store.Summaries.ListAsync(workspace, session, Ct)).ShouldBeEmpty();
        await ShouldConflictAsync(
            () => store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("bob", "late")]), Ct), "session is being deleted");

        // The first worker crashes; its lease expires and another worker resumes the same item.
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var second = (await AcquireAsync(store, WorkTaskType.Deletion, "worker-2")).ShouldNotBeNull();
        second.Items.Single().Id.ShouldBe(first.Items.Single().Id);
        await store.Deletion.CancelOtherWorkAsync(second, target, Ct);
        var batches = 0;
        while (await store.Deletion.PurgeBatchAsync(second, target, 3, Ct))
        {
            batches++;
            batches.ShouldBeLessThan(20);
            await ShouldConflictAsync(
                () => store.Sessions.AddPeersAsync(workspace, session, Members("carol"), Ct), "session is being deleted");
        }

        (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Summaries.ListAsync(workspace, session, Ct)).ShouldBeEmpty();
        (await PendingEmbeddingsAsync(store, workspace)).ShouldBe(0);
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Total.ShouldBe(0);

        await store.Deletion.RemoveAsync(second, target, Ct);
        (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldBeNull();
        (await store.Deletion.ListJobsAsync(workspace, new PageRequest(), Ct)).Items.Single().Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task Purge_StaleFencingToken_Rejected()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await PopulatedSessionAsync(store);
        var target = DeletionTarget.Session(workspace, session);
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var stale = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var current = (await AcquireAsync(store, WorkTaskType.Deletion, "worker-2")).ShouldNotBeNull();

        await Should.ThrowAsync<LeaseLostException>(() => store.Deletion.CancelOtherWorkAsync(stale, target, Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.Deletion.PurgeBatchAsync(stale, target, 500, Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.Deletion.RemoveAsync(stale, target, Ct));

        // A held lease is still rejected for a target whose deletion item it does not hold.
        var (otherWorkspace, otherSession) = await NewSessionAsync(store);
        await store.Deletion.BeginDeleteSessionAsync(otherWorkspace, otherSession, Ct);
        await Should.ThrowAsync<LeaseLostException>(
            () => store.Deletion.PurgeBatchAsync(current, DeletionTarget.Session(otherWorkspace, otherSession), 500, Ct));
        await Should.ThrowAsync<LeaseLostException>(
            () => store.Deletion.PurgeBatchAsync(current, DeletionTarget.Workspace(workspace), 500, Ct));

        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(10);
        (await store.Summaries.ListAsync(workspace, session, Ct)).Count.ShouldBe(1);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.Deletion.PurgeBatchAsync(current, target, 0, Ct));
        await RunJobAsync(store, current, target);
    }

    [Fact]
    public async Task Remove_FreesNameForReuse()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await PopulatedSessionAsync(store);
        var grantee = Unique("object");
        await store.Grants.AddAsync(new GrantRecord(grantee, workspace, GrantRoles.Workspace), Ct);
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await RunJobAsync(store, (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull(), DeletionTarget.Session(workspace, session));

        var reused = await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        reused.State.ShouldBe(LifecycleState.Active);
        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Summaries.ListAsync(workspace, session, Ct)).ShouldBeEmpty();
        await store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("bob", "fresh start")]), Ct);
        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Items.Single().Seq.ShouldBe(1);

        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await store.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);
        var sessionJob = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        var workspaceJob = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        await RunJobAsync(store, workspaceJob, DeletionTarget.Workspace(workspace), batchSize: 4);

        (await store.Workspaces.GetAsync(workspace, Ct)).ShouldBeNull();
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(sessionJob, Lease, Ct));
        var recreated = await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        recreated.State.ShouldBe(LifecycleState.Active);
        (await store.Sessions.ListAsync(workspace, null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Peers.ListAsync(workspace, PeerKind.All, null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Deletion.ListJobsAsync(workspace, new PageRequest(), Ct)).Items.ShouldBeEmpty();
        (await PendingEmbeddingsAsync(store, workspace)).ShouldBe(0);
        (await store.Grants.ListAsync(grantee, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionJob_StaysListed_AfterTheSessionIsRemoved()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var acceptance = await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await RunJobAsync(store, (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull(), DeletionTarget.Session(workspace, session));

        var job = (await store.Deletion.ListJobsAsync(workspace, new PageRequest(), Ct)).Items.Single();

        job.Id.ShouldBe(acceptance.JobId);
        job.Target.ShouldBe(DeletionTarget.Session(workspace, session));
        job.Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task SaveSummary_AfterTheSessionIsRemoved_CompletesWithoutWriting()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var messages = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([.. Enumerable.Range(0, 20).Select(i => Msg("bob", $"m{i}"))], Summary: new SummaryIntent(20, 60)),
            Ct);
        var summaryLease = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var job = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        var target = DeletionTarget.Session(workspace, session);

        // Without CancelOtherWorkAsync the summary lease is not fenced, so the save meets a removed session.
        while (await store.Deletion.PurgeBatchAsync(job, target, 500, Ct))
        {
        }

        await store.Deletion.RemoveAsync(job, target, Ct);
        await store.Summaries.SaveAsync(
            summaryLease, workspace, session, [new NewSummary(SummaryKind.Short, "late", messages[19].PublicId, 20, 1)], Ct);

        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(summaryLease, Lease, Ct));
        (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task CancelOtherWork_FencesOtherLeases_KeepsOwn()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(
            workspace, session, new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true) }, Ct);
        await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([Msg("bob", "one"), Msg("bob", "two")], Derive: [DeriveIntent.Derive, DeriveIntent.Derive]),
            Ct);
        var inFlight = (await AcquireAsync(store, WorkTaskType.Representation)).ShouldNotBeNull();
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var own = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();

        await store.Deletion.CancelOtherWorkAsync(own, DeletionTarget.Session(workspace, session), Ct);

        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(inFlight, Lease, Ct));
        await store.WorkQueue.RenewAsync(own, Lease, Ct);
        (await AcquireAsync(store, WorkTaskType.Representation)).ShouldBeNull();
        var totals = (await store.WorkQueue.GetStatusAsync(workspace, null, null, session, Ct)).Totals;
        totals.Pending.ShouldBe(0);
        totals.InProgress.ShouldBe(1);
    }

    [Fact]
    public async Task CancelOtherWork_WorkspaceSupersedesSessionJobs()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var sessionAcceptance = await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await store.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);
        var sessionJob = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        var workspaceJob = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();

        await store.Deletion.CancelOtherWorkAsync(workspaceJob, DeletionTarget.Workspace(workspace), Ct);

        await Should.ThrowAsync<LeaseLostException>(
            () => store.Deletion.PurgeBatchAsync(sessionJob, DeletionTarget.Session(workspace, session), 500, Ct));
        var superseded = (await store.Deletion.ListJobsAsync(workspace, new PageRequest(), Ct)).Items
            .Single(job => job.Id == sessionAcceptance.JobId);
        superseded.Status.ShouldBe(JobStatus.Completed);
        superseded.Error.ShouldBe("superseded_by_workspace_deletion");
        await store.WorkQueue.RenewAsync(workspaceJob, Lease, Ct);
    }

    [Fact]
    public async Task Purge_TombstonesUnsupportedConclusions()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice", "bob"), Ct);
        var stated = (await store.Conclusions.InsertAsync(workspace, [Conclusion("bob likes tea", session)], Ct)).Single();
        var elsewhere = (await store.Conclusions.InsertAsync(workspace, [Conclusion("bob likes jazz")], Ct)).Single();
        var retracted = (await store.Conclusions.InsertAsync(workspace, [Conclusion("bob likes rock")], Ct)).Single();
        var unrelated = (await store.Conclusions.InsertAsync(workspace, [Conclusion("bob likes blues")], Ct)).Single();
        var derived = await store.Conclusions.InsertAsync(
            workspace,
            [
                Conclusion("bob enjoys hot drinks", level: ConclusionLevel.Deductive, sources: [stated.PublicId]),
                Conclusion("bob has calm tastes", level: ConclusionLevel.Inductive, sources: [stated.PublicId, elsewhere.PublicId]),
                Conclusion("bob likes music", level: ConclusionLevel.Inductive, sources: [stated.PublicId, retracted.PublicId]),
                Conclusion("bob likes the blues scene", level: ConclusionLevel.Deductive, sources: [unrelated.PublicId]),
            ],
            Ct);

        // A tombstoned source is no support, whether or not the reconciler has purged it yet; a conclusion that cites
        // none of the deleted session's conclusions is not re-evaluated by its deletion.
        await store.Conclusions.TombstoneAsync(workspace, retracted.PublicId, Ct);
        await store.Conclusions.TombstoneAsync(workspace, unrelated.PublicId, Ct);

        // The reconciler purges both tombstones before the job runs, so "a source is missing" is no proof that this
        // deletion removed it: derived[3] lost its source long ago and must not be re-evaluated.
        await store.Conclusions.PurgeTombstonesAsync(TimeSpan.Zero, Ct);
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);

        await RunJobAsync(store, (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull(), DeletionTarget.Session(workspace, session), batchSize: 1);

        (await store.Conclusions.GetAsync(workspace, stated.PublicId, Ct)).ShouldBeNull();
        (await store.Conclusions.GetAsync(workspace, derived[0].PublicId, Ct)).ShouldBeNull();
        (await store.Conclusions.GetAsync(workspace, derived[1].PublicId, Ct)).ShouldNotBeNull().SourceIds.ShouldBe([elsewhere.PublicId]);
        (await store.Conclusions.GetAsync(workspace, elsewhere.PublicId, Ct)).ShouldNotBeNull();
        (await store.Conclusions.GetAsync(workspace, derived[2].PublicId, Ct)).ShouldBeNull();
        (await store.Conclusions.GetAsync(workspace, derived[3].PublicId, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Purge_TombstonesUnsupportedConclusions_AcrossALeaseChange()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice", "bob"), Ct);
        var stated = (await store.Conclusions.InsertAsync(workspace, [Conclusion("bob likes tea", session)], Ct)).Single();
        var derived = (await store.Conclusions.InsertAsync(
            workspace, [Conclusion("bob enjoys hot drinks", level: ConclusionLevel.Deductive, sources: [stated.PublicId])], Ct)).Single();
        var secondHand = (await store.Conclusions.InsertAsync(
            workspace, [Conclusion("bob is a morning person", level: ConclusionLevel.Inductive, sources: [derived.PublicId])], Ct)).Single();
        var target = DeletionTarget.Session(workspace, session);
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var first = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        await store.Deletion.CancelOtherWorkAsync(first, target, Ct);

        // One row per batch, stopping as soon as the session's conclusion is removed: its dependents are not yet re-evaluated
        // by a provider that defers that step.
        for (var batch = 0; await store.Conclusions.GetAsync(workspace, stated.PublicId, Ct) is not null; batch++)
        {
            batch.ShouldBeLessThan(10);
            (await store.Deletion.PurgeBatchAsync(first, target, 1, Ct)).ShouldBeTrue();
        }

        // The first worker crashes; another resumes the job under a new lease.
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var second = (await AcquireAsync(store, WorkTaskType.Deletion, "worker-2")).ShouldNotBeNull();
        await Should.ThrowAsync<LeaseLostException>(() => store.Deletion.PurgeBatchAsync(first, target, 1, Ct));
        (await store.Deletion.ListJobsAsync(workspace, new PageRequest(), Ct)).Items.Single().Attempts.ShouldBe(2);
        await RunJobAsync(store, second, target, batchSize: 1);

        (await store.Conclusions.GetAsync(workspace, derived.PublicId, Ct)).ShouldBeNull();
        // Tombstoning does not cascade: this one cites a conclusion that was tombstoned, not removed.
        (await store.Conclusions.GetAsync(workspace, secondHand.PublicId, Ct)).ShouldNotBeNull();
    }
}
