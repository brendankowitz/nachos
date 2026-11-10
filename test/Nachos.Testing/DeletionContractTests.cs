using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// Deletion acceptance and the write barrier of <see cref="IDeletionStore"/> (spec §13.1), and job listing. The deletion
/// job's purge is <see cref="DeletionPurgeContractTests"/>. A provider's test project derives from this class and
/// supplies the store.
/// </summary>
public abstract class DeletionContractTests : M3ContractTestBase
{
    private const string SessionDeleting = "session is being deleted";
    private const string WorkspaceDeleting = "workspace is being deleted";

    private static NewConclusion Conclusion(string? session) =>
        new("alice", "bob", session, ConclusionLevel.Explicit, "bob likes tea", null, [], null);

    [Fact]
    public async Task DeleteSession_Repeat_ReturnsSameJob()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);

        var first = await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var second = await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);

        first.AlreadyDeleting.ShouldBeFalse();
        second.ShouldBe(first with { AlreadyDeleting = true });
        (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
        var lease = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        lease.WorkUnitKey.ShouldBe(WorkUnitKeys.Deletion(workspace, DeletionTargetKind.Session, session));
        var item = lease.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(first.JobId);
        item.SessionName.ShouldBe(session);
        DeletionPayload.Read(item).ShouldBe(DeletionTarget.Session(workspace, session));
        await Should.ThrowAsync<NotFoundException>(() => store.Deletion.BeginDeleteSessionAsync(workspace, Unique("missing"), Ct));
    }

    [Fact]
    public async Task DeleteWorkspace_ActiveSessions_Conflicts()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);

        await ShouldConflictAsync(() => store.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct), "workspace has active sessions");
        (await store.Workspaces.GetAsync(workspace, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Active);

        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var accepted = await store.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);
        var repeated = await store.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);

        accepted.AlreadyDeleting.ShouldBeFalse();
        repeated.ShouldBe(accepted with { AlreadyDeleting = true });
        (await store.Workspaces.GetAsync(workspace, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
        await ShouldConflictAsync(() => store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct), WorkspaceDeleting);
        await Should.ThrowAsync<NotFoundException>(() => store.Deletion.BeginDeleteWorkspaceAsync(Unique("ws"), Ct));
    }

    /// <summary>Every write the barrier covers (spec §13.1), plus conclusion create, under a deleting session.</summary>
    [Theory]
    [InlineData("append")]
    [InlineData("append with derive")]
    [InlineData("session get-or-create")]
    [InlineData("session update")]
    [InlineData("add peers")]
    [InlineData("set peers")]
    [InlineData("remove peers")]
    [InlineData("set peer config")]
    [InlineData("message metadata update")]
    [InlineData("conclusion create")]
    public async Task WritesUnderDeleting_Throw(string write)
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice", "bob"), Ct);
        var message = (await store.Messages.AppendAsync(workspace, session, [Msg("bob", "hello")], null, Ct)).Single();
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);

        await ShouldConflictAsync(Write(store, write, workspace, session, message.PublicId), SessionDeleting);

        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(1);
        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Total.ShouldBe(2);
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Total.ShouldBe(0);
    }

    /// <summary>Every write under a deleting workspace, including get-or-create of a new session or peer.</summary>
    [Theory]
    [InlineData("append")]
    [InlineData("session get-or-create")]
    [InlineData("new session get-or-create")]
    [InlineData("peer get-or-create")]
    [InlineData("peer update")]
    [InlineData("workspace get-or-create")]
    [InlineData("workspace update")]
    [InlineData("conclusion create")]
    [InlineData("peer card set")]
    [InlineData("grant add")]
    public async Task WritesUnderDeletingWorkspace_Throw(string write)
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice", "bob"), Ct);
        var message = (await store.Messages.AppendAsync(workspace, session, [Msg("bob", "hello")], null, Ct)).Single();
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await store.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);

        await ShouldConflictAsync(Write(store, write, workspace, session, message.PublicId), WorkspaceDeleting);

        (await store.Sessions.ListAsync(workspace, null, new PageRequest(), Ct)).Total.ShouldBe(1);
        (await store.Peers.ListAsync(workspace, PeerKind.All, null, new PageRequest(), Ct)).Total.ShouldBe(2);
    }

    private static Func<Task> Write(IMemoryStore store, string write, string workspace, string session, string messageId) => write switch
    {
        "append" => () => store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("bob", "late")]), Ct),
        "append with derive" => () => store.Messages.AppendAsync(workspace, session, [Msg("bob", "late")], null, [DeriveIntent.Derive], Ct),
        "session get-or-create" => () => store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct),
        "new session get-or-create" => () => store.Sessions.GetOrCreateAsync(workspace, Unique("sess"), null, null, null, Ct),
        "session update" => () => store.Sessions.UpdateAsync(workspace, session, new JsonObject { ["k"] = 1 }, null, Ct),
        "add peers" => () => store.Sessions.AddPeersAsync(workspace, session, Members("carol"), Ct),
        "set peers" => () => store.Sessions.SetPeersAsync(workspace, session, Members("carol"), Ct),
        "remove peers" => () => store.Sessions.RemovePeersAsync(workspace, session, ["alice"], Ct),
        "set peer config" => () => store.Sessions.SetPeerConfigAsync(workspace, session, "alice", new SessionPeerConfig(false, false), Ct),
        "message metadata update" => () => store.Messages.UpdateMetadataAsync(workspace, session, messageId, new JsonObject { ["k"] = 1 }, Ct),
        "conclusion create" => () => store.Conclusions.InsertAsync(workspace, [Conclusion(session)], Ct),
        "peer get-or-create" => () => store.Peers.GetOrCreateAsync(workspace, "carol", null, null, Ct),
        "peer update" => () => store.Peers.UpdateAsync(workspace, "alice", new JsonObject { ["k"] = 1 }, null, Ct),
        "workspace get-or-create" => () => store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct),
        "workspace update" => () => store.Workspaces.UpdateAsync(workspace, new JsonObject { ["k"] = 1 }, null, Ct),
        "peer card set" => () => store.PeerCards.SetAsync(workspace, "alice", "bob", ["likes tea"], PeerCardAuthor.Api, Ct),
        "grant add" => () => store.Grants.AddAsync(new GrantRecord(Unique("object"), workspace, GrantRoles.Workspace), Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(write), write, "Unknown write."),
    };

    [Fact]
    public async Task SearchAndEmbeddingBacklog_ExcludeResourcesBeingDeleted()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, deleting) = await NewSessionAsync(store);
        var kept = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, kept, null, null, null, Ct);
        var embedded = new Dictionary<(string, int), float[]>();
        foreach (var session in new[] { deleting, kept })
        {
            var message = (await store.Messages.AppendAsync(
                workspace, session, new MessageAppend([Msg("bob", $"apple in {session}")], Chunks: [["apple", "pending"]]), Ct)).Single();
            embedded[(message.PublicId, 0)] = Vector(0);
        }

        await EmbedChunksAsync(store, embedded);
        var request = new MessageSearchRequest(workspace, new MessageSearchScope.Workspace(), null);
        var perspective = request with { Scope = new MessageSearchScope.PeerPerspective("bob") };
        var countBefore = await store.Embeddings.CountPendingAsync(Ct);

        await store.Deletion.BeginDeleteSessionAsync(workspace, deleting, Ct);

        foreach (var scoped in new[] { request, perspective, request with { Scope = new MessageSearchScope.Session(deleting) } })
        {
            var lexical = await store.Search.LexicalAsync(scoped, ["apple"], 10, Ct);
            var vector = await store.Search.VectorAsync(scoped, Vector(0), 10, null, Ct);
            lexical.Concat(vector).ShouldAllBe(hit => hit.Message.SessionName == kept);
        }

        var pending = (await store.Embeddings.ListPendingAsync(100_000, Ct)).Where(p => p.WorkspaceName == workspace).ToList();
        pending.Count.ShouldBe(1);
        (await store.Embeddings.CountPendingAsync(Ct)).ShouldBe(countBefore - 1);

        await store.Deletion.BeginDeleteSessionAsync(workspace, kept, Ct);
        await store.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);
        (await store.Search.LexicalAsync(request, ["apple"], 10, Ct)).ShouldBeEmpty();
        (await store.Search.VectorAsync(request, Vector(0), 10, null, Ct)).ShouldBeEmpty();
        (await store.Embeddings.ListPendingAsync(100_000, Ct)).ShouldNotContain(p => p.WorkspaceName == workspace);
    }

    [Fact]
    public async Task WorkerCommitsUnderDeleting_AreDiscardedAndCompleted()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(
            workspace, session, new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true) }, Ct);
        var messages = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend(
                [.. Enumerable.Range(0, 20).Select(i => Msg("bob", $"m{i}"))],
                Derive: [.. Enumerable.Repeat(DeriveIntent.Derive, 20)],
                Summary: new SummaryIntent(20, 60)),
            Ct);
        var summaryLease = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();
        var derivationLease = (await AcquireAsync(store, WorkTaskType.Representation)).ShouldNotBeNull();

        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await store.Summaries.SaveAsync(
            summaryLease, workspace, session, [new NewSummary(SummaryKind.Short, "late", messages[19].PublicId, 20, 1)], Ct);
        await store.Conclusions.CommitDerivationAsync(
            derivationLease, new DerivationChanges(workspace, [Conclusion(session)], [], []), Ct);

        (await store.Summaries.ListAsync(workspace, session, Ct)).ShouldBeEmpty();
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Total.ShouldBe(0);
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(summaryLease, Lease, Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(derivationLease, Lease, Ct));
        (await store.WorkQueue.GetStatusAsync(workspace, null, null, session, Ct)).Totals.Completed.ShouldBe(2);
    }

    [Fact]
    public async Task ListJobs_ShowsDeletionsWithStatus_Paged()
    {
        var (store, clock) = await NewStoreAsync();
        var workspace = await NewWorkspaceAsync(store);
        var sessions = new[] { Unique("sess"), Unique("sess"), Unique("sess") };
        foreach (var session in sessions)
        {
            await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
            await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var lease = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        await store.WorkQueue.FailAsync(lease, "boom", TimeSpan.Zero, maxAttempts: 1, Ct);
        var inProgress = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();

        var page = await store.Deletion.ListJobsAsync(workspace, new PageRequest(1, 2), Ct);

        page.Total.ShouldBe(3);
        page.Pages.ShouldBe(2);
        page.Items.Select(job => job.Target).ShouldBe([DeletionTarget.Session(workspace, sessions[0]), DeletionTarget.Session(workspace, sessions[1])]);
        page.Items[0].ShouldBe(new JobRecord(
            lease.Items[0].Id, WorkTaskType.Deletion, DeletionTarget.Session(workspace, sessions[0]), JobStatus.DeadLettered, 1, "boom", Start, Start.AddSeconds(3)));
        page.Items[1].Status.ShouldBe(JobStatus.InProgress);
        page.Items[1].Id.ShouldBe(inProgress.Items[0].Id);
        var last = (await store.Deletion.ListJobsAsync(workspace, new PageRequest(1, 1, Reverse: true), Ct)).Items.Single();
        last.Status.ShouldBe(JobStatus.Pending);
        last.Attempts.ShouldBe(0);
        last.Error.ShouldBeNull();
        last.CompletedAt.ShouldBeNull();
        (await store.Deletion.ListJobsAsync(await NewWorkspaceAsync(store), new PageRequest(), Ct)).Items.ShouldBeEmpty();
        await Should.ThrowAsync<NotFoundException>(() => store.Deletion.ListJobsAsync(Unique("ws"), new PageRequest(), Ct));
    }
}
