using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Deletion;
using Nachos.Core.Tests.Worker;
using Nachos.Core.Worker;
using Nachos.DataLayer.InMemory;
using Shouldly;

namespace Nachos.Core.Tests.Deletion;

/// <summary>The deletion job handler (spec §13.1, "The deletion job") over the in-memory provider.</summary>
public sealed class DeletionServiceTests
{
    private const string Workspace = "W";
    private const string Session = "S";
    private const string Other = "S2";

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));

    private readonly ObservedStore _store;

    public DeletionServiceTests() => _store = new ObservedStore(new InMemoryMemoryStore(_clock));

    [Fact]
    public async Task SessionDelete_PurgesEverythingAttributed()
    {
        await SeedAsync(Session, Other);
        await PopulateAsync(Session, messages: 7);
        await PopulateAsync(Other, messages: 2);
        var kept = (await _store.Conclusions.InsertAsync(Workspace, [Explicit("bob likes jazz", session: null)], Ct)).Single();
        var acceptance = await _store.Deletion.BeginDeleteSessionAsync(Workspace, Session, Ct);

        await Handler(batchSize: 3).HandleAsync(await AcquireDeletionAsync(), Ct);

        (await _store.Sessions.GetAsync(Workspace, Session, Ct)).ShouldBeNull();
        _store.Renewals.ShouldBeGreaterThan(0, "the lease is renewed between batches");
        (await _store.Messages.ListAsync(Workspace, Other, null, new PageRequest(), Ct)).Total.ShouldBe(2);
        (await _store.Summaries.ListAsync(Workspace, Other, Ct)).Count.ShouldBe(1);
        var conclusions = await _store.Conclusions.ListAsync(Workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct);
        conclusions.Items.Select(c => c.SessionName).ShouldBe([Other, null], ignoreOrder: true);
        conclusions.Items.ShouldContain(c => c.PublicId == kept.PublicId);
        var pending = await _store.Embeddings.ListPendingAsync(1000, Ct);
        pending.Where(p => p.Kind == PendingEmbeddingKind.MessageChunk).Count().ShouldBe(4, "only the other session's chunks are left");
        var status = await _store.WorkQueue.GetStatusAsync(Workspace, null, null, null, Ct);
        // Only the job's own item is left under the session's name, as its record.
        status.Sessions[Session].ShouldBe(new QueueCounts(Pending: 0, InProgress: 0, Completed: 1, DeadLettered: 0));
        var job = (await _store.Deletion.ListJobsAsync(Workspace, new PageRequest(), Ct)).Items.ShouldHaveSingleItem();
        job.Id.ShouldBe(acceptance.JobId);
        job.Status.ShouldBe(JobStatus.Completed);
        job.Error.ShouldBeNull();

        var reused = await _store.Sessions.GetOrCreateAsync(Workspace, Session, null, null, null, Ct);
        reused.State.ShouldBe(LifecycleState.Active);
        (await _store.Messages.ListAsync(Workspace, Session, null, new PageRequest(), Ct)).Total.ShouldBe(0);
    }

    [Fact]
    public async Task UnsupportedConclusion_Tombstoned()
    {
        await SeedAsync(Session);
        var stated = (await _store.Conclusions.InsertAsync(Workspace, [Explicit("bob likes tea", Session)], Ct)).Single();
        var elsewhere = (await _store.Conclusions.InsertAsync(Workspace, [Explicit("bob likes jazz", session: null)], Ct)).Single();
        var derived = await _store.Conclusions.InsertAsync(
            Workspace,
            [
                Derived("bob enjoys hot drinks", stated.PublicId),
                Derived("bob has calm tastes", stated.PublicId, elsewhere.PublicId),
            ],
            Ct);
        await _store.Deletion.BeginDeleteSessionAsync(Workspace, Session, Ct);

        await Handler(batchSize: 1).HandleAsync(await AcquireDeletionAsync(), Ct);

        (await _store.Conclusions.GetAsync(Workspace, stated.PublicId, Ct)).ShouldBeNull();
        (await _store.Conclusions.GetAsync(Workspace, derived[0].PublicId, Ct)).ShouldBeNull("its only source was deleted");
        (await _store.Conclusions.GetAsync(Workspace, derived[1].PublicId, Ct)).ShouldNotBeNull().SourceIds.ShouldBe([elsewhere.PublicId]);
        var listed = await _store.Conclusions.ListAsync(Workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct);
        listed.Items.Select(c => c.PublicId).ShouldBe([elsewhere.PublicId, derived[1].PublicId], ignoreOrder: true);
        // D7: M3 enqueues no dream for the tombstoned conclusion.
        (await _store.WorkQueue.GetStatusAsync(Workspace, null, null, null, Ct)).Totals.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task WorkspaceDelete_SupersedesChildJobs()
    {
        await SeedAsync(Session);
        await PopulateAsync(Session, messages: 3);
        await _store.Deletion.BeginDeleteSessionAsync(Workspace, Session, Ct);
        await _store.Deletion.BeginDeleteWorkspaceAsync(Workspace, Ct);
        var sessionJob = await AcquireDeletionAsync();
        var workspaceJob = await AcquireDeletionAsync();
        workspaceJob.WorkUnitKey.ShouldBe(DeletionTarget.Workspace(Workspace).WorkUnitKey);

        await Handler(batchSize: 4).HandleAsync(workspaceJob, Ct);

        (await _store.Workspaces.GetAsync(Workspace, Ct)).ShouldBeNull();
        await Should.ThrowAsync<LeaseLostException>(() => Handler().HandleAsync(sessionJob, Ct));
        await _store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        (await _store.Deletion.ListJobsAsync(Workspace, new PageRequest(), Ct)).Items.ShouldBeEmpty();
        (await _store.Sessions.ListAsync(Workspace, null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await _store.Peers.ListAsync(Workspace, PeerKind.All, null, new PageRequest(), Ct)).Total.ShouldBe(0);
    }

    [Fact]
    public async Task OwnLeaseSurvivesCancel()
    {
        await SeedAsync(Session);
        await PopulateAsync(Session, messages: 2);
        var inFlight = (await AcquireAsync(WorkTaskType.Representation)).ShouldNotBeNull();
        await _store.Deletion.BeginDeleteSessionAsync(Workspace, Session, Ct);
        var own = await AcquireDeletionAsync();

        await Handler().HandleAsync(own, Ct);

        await Should.ThrowAsync<LeaseLostException>(() => _store.WorkQueue.RenewAsync(inFlight, Lease, Ct));
        var job = (await _store.Deletion.ListJobsAsync(Workspace, new PageRequest(), Ct)).Items.ShouldHaveSingleItem();
        job.Status.ShouldBe(JobStatus.Completed);
        job.Attempts.ShouldBe(1);
        (await _store.Sessions.GetAsync(Workspace, Session, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task LeaseLostBetweenBatches_StopsWithoutWriting()
    {
        await SeedAsync(Session);
        await PopulateAsync(Session, messages: 6);
        await _store.Deletion.BeginDeleteSessionAsync(Workspace, Session, Ct);
        var lease = await AcquireDeletionAsync();
        _store.RenewalFailure = l => new LeaseLostException(l.WorkUnitKey);

        await Should.ThrowAsync<LeaseLostException>(() => Handler(batchSize: 1).HandleAsync(lease, Ct));

        // One batch of one row went before the renewal failed; nothing after it.
        (await _store.Messages.ListAsync(Workspace, Session, null, new PageRequest(), Ct)).Total.ShouldBe(6);
        (await _store.Sessions.GetAsync(Workspace, Session, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
        (await _store.Summaries.ListAsync(Workspace, Session, Ct)).Count.ShouldBe(1, "summaries go after every chunk");
    }

    [Fact]
    public async Task DeadLettered_LeavesDeleting()
    {
        await using var fixture = new WorkerFixture(
            handlerType: null,
            configure: builder => builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkHandler, DeletionService>()),
            configureNachos: options => options.Deriver.MaxAttempts = 3);
        await fixture.SeedAsync();
        // The message stands in for provider text that must never reach W/jobs (a server name, a connection string).
        fixture.Store.PurgeFailure = new InvalidOperationException("Server=tcp:secret-host.example;Password=hunter2 the purge failed");
        var acceptance = await fixture.Store.Deletion.BeginDeleteSessionAsync(WorkerFixture.WorkspaceId, WorkerFixture.SessionId, Ct);

        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(
            fixture.Clock,
            async () => (await fixture.Store.Deletion.ListJobsAsync(WorkerFixture.WorkspaceId, new PageRequest(), Ct)).Items
                .Single().Status == JobStatus.DeadLettered,
            TimeSpan.FromMinutes(1));

        var job = (await fixture.Store.Deletion.ListJobsAsync(WorkerFixture.WorkspaceId, new PageRequest(), Ct)).Items.Single();
        job.Id.ShouldBe(acceptance.JobId);
        job.Attempts.ShouldBe(3);
        job.Error.ShouldBe("handler_failed: System.InvalidOperationException");
        (await new Nachos.Core.Jobs.JobService(fixture.Store).ListAsync(WorkerFixture.WorkspaceId, new PageRequest(), Ct))
            .Items.Single().Error.ShouldBe("handler_failed: System.InvalidOperationException");
        fixture.Store.Failures.Count.ShouldBe(3, "every attempt failed the same item, with backoff");
        (await fixture.Store.Sessions.GetAsync(WorkerFixture.WorkspaceId, WorkerFixture.SessionId, Ct))
            .ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
        var conflict = await Should.ThrowAsync<ConflictException>(() => fixture.Store.Messages.AppendAsync(
            WorkerFixture.WorkspaceId, WorkerFixture.SessionId, [new NewMessage("alice", "late", 1, null, null)], null, null, Ct));
        conflict.Message.ShouldBe("session is being deleted");
    }

    [Fact]
    public async Task NotADeletionItem_Throws()
    {
        await SeedAsync(Session);
        await PopulateAsync(Session, messages: 1);
        var lease = (await AcquireAsync(WorkTaskType.Representation)).ShouldNotBeNull();

        await Should.ThrowAsync<FormatException>(() => Handler().HandleAsync(lease, Ct));
    }

    private DeletionService Handler(int batchSize = 500)
    {
        var nachos = new NachosOptions();
        nachos.Deriver.LeaseDuration = Lease;
        return new DeletionService(
            _store, Options.Create(nachos), Options.Create(new DeletionOptions { BatchSize = batchSize }), NullLogger<DeletionService>.Instance);
    }

    private async Task SeedAsync(params string[] sessions)
    {
        await _store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        foreach (var session in sessions)
        {
            await _store.Sessions.GetOrCreateAsync(
                Workspace,
                session,
                null,
                null,
                new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true), ["bob"] = new() },
                Ct);
        }
    }

    /// <summary>Messages with two chunks each and derivation work, a short summary, and an explicit conclusion stated there.</summary>
    private async Task PopulateAsync(string session, int messages)
    {
        var appended = await _store.Messages.AppendAsync(
            Workspace,
            session,
            new MessageAppend(
                [.. Enumerable.Range(0, messages).Select(i => new NewMessage("bob", $"{session} message {i}", 3, null, null))],
                Derive: [.. Enumerable.Range(0, messages).Select(_ => DeriveIntent.Derive)],
                Chunks: [.. Enumerable.Range(0, messages).Select(i => (IReadOnlyList<string>)[$"{session} m{i} a", $"{session} m{i} b"])],
                Summary: new SummaryIntent(messages, 1000)),
            Ct);
        var summary = (await AcquireAsync(WorkTaskType.Summary)).ShouldNotBeNull();
        await _store.Summaries.SaveAsync(
            summary, Workspace, session, [new NewSummary(SummaryKind.Short, "summary", appended[^1].PublicId, messages, 1)], Ct);
        await _store.Conclusions.InsertAsync(Workspace, [Explicit($"bob said things in {session}", session)], Ct);
    }

    private static NewConclusion Explicit(string content, string? session) =>
        new("alice", "bob", session, ConclusionLevel.Explicit, content, null, [], null);

    private static NewConclusion Derived(string content, params string[] sources) =>
        new("alice", "bob", null, ConclusionLevel.Deductive, content, null, sources, null);

    private Task<WorkLease?> AcquireAsync(WorkTaskType type) =>
        _store.WorkQueue.TryAcquireAsync("worker", new Dictionary<WorkTaskType, WorkBatchPolicy> { [type] = WorkBatchPolicy.Immediate }, Lease, Ct);

    private async Task<WorkLease> AcquireDeletionAsync() => (await AcquireAsync(WorkTaskType.Deletion)).ShouldNotBeNull();
}
