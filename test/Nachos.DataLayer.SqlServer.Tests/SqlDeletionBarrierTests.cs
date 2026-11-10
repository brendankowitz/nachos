using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.DataLayer.SqlServer.Storage;
using Nachos.Testing;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The spec §13.1 write-barrier races (Review Focus 4). Each race is made deterministic: one side is paused inside its
/// transaction, holding its locks (acceptance by <see cref="SqlServerOptions.BeforeLifecycleTransition"/>, an append by its
/// idempotency serializer, a session insert or a worker commit by a conflicting uncommitted row), and the other side is
/// started only once the database shows the paused side's locks blocking it.
/// </summary>
[Collection(M2StoreContracts.Name)]
public sealed class SqlDeletionBarrierTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private SqlTestDatabase? _database;

    private static CancellationToken Ct => CancellationToken.None;

    private SqlTestDatabase Database => _database ?? throw new InvalidOperationException("The database is not initialized.");

    public async Task InitializeAsync()
    {
        _database = await SqlTestDatabase.GetAsync(fixture, "deletion-barrier");
        await Database.ResetQueueAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void BeforeLifecycleTransition_IsNotPublic()
    {
        typeof(SqlServerOptions).GetProperty(nameof(SqlServerOptions.BeforeLifecycleTransition), BindingFlags.Public | BindingFlags.Instance)
            .ShouldBeNull();
    }

    /// <summary>Under autocommit the barrier's S locks would end with their statements, so it refuses to run without a transaction.</summary>
    [Fact]
    public async Task Admission_WithoutTransaction_Throws()
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var (workspace, session) = await NewSessionAsync(store);
        await using var db = new NachosDbContext(new DbContextOptionsBuilder<NachosDbContext>().UseSqlServer(Database.ConnectionString).Options);
        const string Message = "The deletion write barrier must run inside the write's transaction.";

        (await Should.ThrowAsync<InvalidOperationException>(() => WriteAdmission.AdmitWorkspaceAsync(db, workspace, Ct))).Message.ShouldBe(Message);
        (await Should.ThrowAsync<InvalidOperationException>(() => WriteAdmission.AdmitSessionForMembershipAsync(db, workspace, session, Ct)))
            .Message.ShouldBe(Message);
        (await Should.ThrowAsync<InvalidOperationException>(() => WriteAdmission.AdmitWorkerAsync(db, workspace, [session], [], Ct)))
            .Message.ShouldBe(Message);
        (await Should.ThrowAsync<InvalidOperationException>(() => WriteAdmission.LockParentsAsync(db, [(workspace, session)], Ct)))
            .Message.ShouldBe(Message);

        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        (await WriteAdmission.AdmitSessionAsync(db, workspace, session, Ct)).SessionId.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Membership writers use the session row's update lock as their barrier: one past it holds acceptance off until it
    /// commits. The writer is paused on an uncommitted peer of the name it adds.
    /// </summary>
    [Fact]
    public async Task Membership_BeforeAcceptance_HoldsItOff()
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice"), Ct);

        await using var blocker = await OpenAsync();
        await using var blocking = (SqlTransaction)await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            blocking,
            "INSERT dbo.Peers (WorkspaceId, Name, IsInternal, Metadata, Configuration, CreatedAt) SELECT Id, N'carol', 0, N'{}', N'{}', SYSDATETIMEOFFSET() FROM dbo.Workspaces WHERE Name = @workspace",
            ("@workspace", workspace));
        var adding = store.Sessions.AddPeersAsync(workspace, session, Members("carol"), Ct);
        await WaitForLockWaitersAsync(1);
        var acceptance = store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await WaitForLockWaitersAsync(2);
        await blocking.RollbackAsync();

        await adding;
        (await acceptance).AlreadyDeleting.ShouldBeFalse();
        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Items.Select(peer => peer.Name)
            .ShouldBe(["alice", "carol"], ignoreOrder: true);
        (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
    }

    /// <summary>
    /// A session update is one statement that takes the row's exclusive lock itself, so it has no point to pause between
    /// its check and its write. This test pins the queue ordering only: an uncommitted update lock on the row queues the
    /// update ahead of the acceptance, the update lands, and the acceptance, granted next, marks the session. The barrier
    /// predicate itself (<c>LifecycleState &lt;&gt; 2</c>) is pinned by the acceptance-first case of
    /// <see cref="SessionAcceptance_vs_Reactivation"/>.
    /// </summary>
    [Fact]
    public async Task SessionUpdate_BeforeAcceptance_HoldsItOff()
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var (workspace, session) = await NewSessionAsync(store);

        await using var blocker = await OpenAsync();
        await using var blocking = (SqlTransaction)await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            blocking,
            "SELECT Id FROM dbo.Sessions WITH (UPDLOCK, ROWLOCK) WHERE WorkspaceId = (SELECT Id FROM dbo.Workspaces WHERE Name = @workspace) AND Name = @session",
            ("@workspace", workspace),
            ("@session", session));
        var update = store.Sessions.UpdateAsync(workspace, session, new JsonObject { ["first"] = true }, null, Ct);
        await WaitForLockWaitersAsync(1);
        var acceptance = store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await WaitForLockWaitersAsync(2);
        await blocking.RollbackAsync();

        (await update).Metadata["first"]!.GetValue<bool>().ShouldBeTrue();
        (await acceptance).AlreadyDeleting.ShouldBeFalse();
        var stored = (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldNotBeNull();
        stored.State.ShouldBe(LifecycleState.Deleting);
        stored.Metadata["first"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Theory]
    [InlineData("acceptance first")]
    [InlineData("create first")]
    public async Task WorkspaceAcceptance_vs_SessionCreate_BothOrders(string order)
    {
        var pause = new Pause();
        var (store, paused) = Stores(pause);
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        var session = Unique("sess");

        if (order == "acceptance first")
        {
            var acceptance = paused.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);
            await pause.Reached;
            var create = store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
            await WaitForLockWaitersAsync(1);
            pause.Release();

            (await acceptance).AlreadyDeleting.ShouldBeFalse();
            (await Should.ThrowAsync<ConflictException>(create)).Message.ShouldBe("workspace is being deleted");
            (await store.Workspaces.GetAsync(workspace, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
            (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldBeNull();
            return;
        }

        // The create takes its S lock on the workspace, then waits on an uncommitted row of the same name.
        await using var blocker = await OpenAsync();
        await using var blocking = (SqlTransaction)await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            blocking,
            "INSERT dbo.Sessions (WorkspaceId, Name, LifecycleState, CreatedAt) SELECT Id, @session, 0, SYSDATETIMEOFFSET() FROM dbo.Workspaces WHERE Name = @workspace",
            ("@session", session),
            ("@workspace", workspace));
        var created = store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        await WaitForLockWaitersAsync(1);
        var refused = paused.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);
        await WaitForLockWaitersAsync(2);
        await blocking.RollbackAsync();

        (await created).State.ShouldBe(LifecycleState.Active);
        (await Should.ThrowAsync<ConflictException>(refused)).Message.ShouldBe("workspace has active sessions");
        pause.Reached.IsCompleted.ShouldBeFalse();
        (await store.Workspaces.GetAsync(workspace, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Active);
        (await DeletionItemsAsync(workspace)).ShouldBe(0);
    }

    [Theory]
    [InlineData("acceptance first")]
    [InlineData("append first")]
    public async Task SessionAcceptance_vs_Append(string order)
    {
        var pause = new Pause();
        var (store, paused) = Stores(pause);
        var (workspace, session) = await NewSessionAsync(store);

        if (order == "acceptance first")
        {
            var acceptance = paused.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
            await pause.Reached;
            var append = store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("late")], Chunks: [["late"]]), Ct);
            await WaitForLockWaitersAsync(1);
            pause.Release();

            (await acceptance).AlreadyDeleting.ShouldBeFalse();
            (await Should.ThrowAsync<ConflictException>(append)).Message.ShouldBe("session is being deleted");
            (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(0);
            (await ScalarAsync("SELECT COUNT(*) FROM dbo.MessageEmbeddings AS e JOIN dbo.Sessions AS s ON s.Id = e.SessionId WHERE s.Name = @name", session))
                .ShouldBe(0);
            return;
        }

        // The append is paused in its serializer, inside its transaction, holding the session row.
        using var serializing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var idempotency = new IdempotencyWrite(
            Unique("key"),
            "hash",
            201,
            records =>
            {
                serializing.Set();
                release.Wait(TimeSpan.FromMinutes(1));
                return "{}";
            },
            TimeSpan.FromHours(1));
        var appended = Task.Run(() => store.Messages.AppendAsync(
            workspace, session, new MessageAppend([Msg("first")], idempotency, Chunks: [["first"]]), Ct));
        await Task.Run(() => serializing.Wait(TimeSpan.FromMinutes(1)).ShouldBeTrue());
        var accepted = store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await WaitForLockWaitersAsync(1);
        release.Set();

        (await appended).ShouldHaveSingleItem();
        (await accepted).AlreadyDeleting.ShouldBeFalse();
        (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(1);
        await Should.ThrowAsync<ConflictException>(
            () => store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("later")]), Ct));
    }

    /// <summary>
    /// The writes that reactivate or change a session's own state: a former member's return (no write path sets a session
    /// row back to active; membership reactivation is the reactivation the store has) and the session-row update.
    /// </summary>
    [Theory]
    [InlineData("member reactivation")]
    [InlineData("session update")]
    public async Task SessionAcceptance_vs_Reactivation(string write)
    {
        var pause = new Pause();
        var (store, paused) = Stores(pause);
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice", "bob"), Ct);
        await store.Sessions.RemovePeersAsync(workspace, session, ["alice"], Ct);

        var acceptance = paused.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await pause.Reached;
        Task writing = write == "member reactivation"
            ? store.Sessions.AddPeersAsync(workspace, session, Members("alice"), Ct)
            : store.Sessions.UpdateAsync(workspace, session, new JsonObject { ["late"] = true }, null, Ct);
        await WaitForLockWaitersAsync(1);
        pause.Release();

        (await acceptance).AlreadyDeleting.ShouldBeFalse();
        (await Should.ThrowAsync<ConflictException>(writing)).Message.ShouldBe("session is being deleted");
        var stored = (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldNotBeNull();
        stored.State.ShouldBe(LifecycleState.Deleting);
        stored.Metadata.ContainsKey("late").ShouldBeFalse();
        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Items.Select(peer => peer.Name).ShouldBe(["bob"]);
    }

    [Fact]
    public async Task LateDerivationCommit_AfterAcceptance_Discarded()
    {
        var pause = new Pause();
        var (store, paused) = Stores(pause);
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(
            workspace, session, new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true) }, Ct);
        await store.Messages.AppendAsync(workspace, session, [Msg("hello")], null, [DeriveIntent.Derive], Ct);
        var lease = (await AcquireAsync(store, WorkTaskType.Representation)).ShouldNotBeNull();

        var acceptance = paused.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await pause.Reached;
        var commit = store.Conclusions.CommitDerivationAsync(
            lease,
            new DerivationChanges(
                workspace, [new NewConclusion("alice", "bob", session, ConclusionLevel.Explicit, "bob says hello", null, [], null)], [], []),
            Ct);
        await WaitForLockWaitersAsync(1);
        pause.Release();

        await acceptance;
        await commit;
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await ItemOutcomeAsync(lease.Items.ShouldHaveSingleItem().Id)).ShouldBe("2:resource_deleted");
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(lease, Lease, Ct));
    }

    [Fact]
    public async Task LateSummarySave_AfterAcceptance_Discarded()
    {
        var pause = new Pause();
        var (store, paused) = Stores(pause);
        var (workspace, session) = await NewSessionAsync(store);
        var messages = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([.. Enumerable.Range(0, 20).Select(i => Msg($"m{i}"))], Summary: new SummaryIntent(20, 60)),
            Ct);
        var lease = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();

        var acceptance = paused.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await pause.Reached;
        var save = store.Summaries.SaveAsync(
            lease, workspace, session, [new NewSummary(SummaryKind.Short, "late", messages[19].PublicId, 20, 1)], Ct);
        await WaitForLockWaitersAsync(1);
        pause.Release();

        await acceptance;
        await save;
        (await store.Summaries.ListAsync(workspace, session, Ct)).ShouldBeEmpty();
        (await ItemOutcomeAsync(lease.Items.ShouldHaveSingleItem().Id)).ShouldBe("2:resource_deleted");
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(lease, Lease, Ct));
    }

    /// <summary>
    /// The other order: a commit past its barrier holds its S locks to commit, so an acceptance waits for it, and the
    /// result lands before the session is marked. The commit is paused on an uncommitted collection of the same key.
    /// </summary>
    [Fact]
    public async Task DerivationCommit_BeforeAcceptance_HoldsItOff()
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(
            workspace, session, new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true) }, Ct);
        await store.Messages.AppendAsync(workspace, session, [Msg("hello")], null, [DeriveIntent.Derive], Ct);
        var lease = (await AcquireAsync(store, WorkTaskType.Representation)).ShouldNotBeNull();

        await using var blocker = await OpenAsync();
        await using var blocking = (SqlTransaction)await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            blocking,
            """
            INSERT dbo.Collections (WorkspaceId, ObserverPeerId, ObservedPeerId, CreatedAt)
            SELECT w.Id, o.Id, d.Id, SYSDATETIMEOFFSET() FROM dbo.Workspaces AS w
            JOIN dbo.Peers AS o ON o.WorkspaceId = w.Id AND o.Name = N'alice'
            JOIN dbo.Peers AS d ON d.WorkspaceId = w.Id AND d.Name = N'bob'
            WHERE w.Name = @workspace
            """,
            ("@workspace", workspace));
        var commit = store.Conclusions.CommitDerivationAsync(
            lease,
            new DerivationChanges(
                workspace, [new NewConclusion("alice", "bob", session, ConclusionLevel.Explicit, "bob says hello", null, [], null)], [], []),
            Ct);
        await WaitForLockWaitersAsync(1);
        var acceptance = store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await WaitForLockWaitersAsync(2);
        await blocking.RollbackAsync();

        await commit;
        (await acceptance).AlreadyDeleting.ShouldBeFalse();
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Total.ShouldBe(1);
        (await ItemOutcomeAsync(lease.Items.ShouldHaveSingleItem().Id)).ShouldBe("2:");
        (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
    }

    /// <summary>As <see cref="DerivationCommit_BeforeAcceptance_HoldsItOff"/>, paused on an uncommitted summary of the same kind.</summary>
    [Fact]
    public async Task SummarySave_BeforeAcceptance_HoldsItOff()
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var (workspace, session) = await NewSessionAsync(store);
        var messages = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([.. Enumerable.Range(0, 20).Select(i => Msg($"m{i}"))], Summary: new SummaryIntent(20, 60)),
            Ct);
        var lease = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();

        await using var blocker = await OpenAsync();
        await using var blocking = (SqlTransaction)await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            blocking,
            """
            INSERT dbo.SessionSummaries (WorkspaceId, SessionId, Kind, Content, CoversThroughMessageId, CoversThroughSeq, TokenCount, CreatedAt)
            SELECT s.WorkspaceId, s.Id, 1, N'blocking', m.Id, m.Seq, 1, SYSDATETIMEOFFSET() FROM dbo.Sessions AS s
            JOIN dbo.Messages AS m ON m.SessionId = s.Id AND m.Seq = 1
            WHERE s.Name = @session
            """,
            ("@session", session));
        var save = store.Summaries.SaveAsync(
            lease, workspace, session, [new NewSummary(SummaryKind.Short, "kept", messages[19].PublicId, 20, 1)], Ct);
        await WaitForLockWaitersAsync(1);
        var acceptance = store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        await WaitForLockWaitersAsync(2);
        await blocking.RollbackAsync();

        await save;
        (await acceptance).AlreadyDeleting.ShouldBeFalse();
        (await ScalarAsync(
                "SELECT COUNT(*) FROM dbo.SessionSummaries AS ss JOIN dbo.Sessions AS s ON s.Id = ss.SessionId WHERE s.Name = @name AND ss.Content = N'kept'",
                session))
            .ShouldBe(1);
        (await ItemOutcomeAsync(lease.Items.ShouldHaveSingleItem().Id)).ShouldBe("2:");
        (await store.Sessions.GetAsync(workspace, session, Ct)).ShouldNotBeNull().State.ShouldBe(LifecycleState.Deleting);
    }

    [Theory]
    [InlineData(DeletionTargetKind.Session)]
    [InlineData(DeletionTargetKind.Workspace)]
    public async Task RepeatedDelete_SameJob(DeletionTargetKind kind)
    {
        var pause = new Pause();
        var (store, paused) = Stores(pause);
        var (workspace, session) = await NewSessionAsync(store);
        if (kind == DeletionTargetKind.Workspace)
        {
            await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        }

        Task<DeletionAcceptance> Delete(IMemoryStore on) => kind == DeletionTargetKind.Session
            ? on.Deletion.BeginDeleteSessionAsync(workspace, session, Ct)
            : on.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);

        var first = Delete(paused);
        await pause.Reached;
        var second = Delete(store);
        await WaitForLockWaitersAsync(1);
        pause.Release();

        var accepted = await first;
        accepted.AlreadyDeleting.ShouldBeFalse();
        (await second).ShouldBe(accepted with { AlreadyDeleting = true });
        (await Delete(store)).ShouldBe(accepted with { AlreadyDeleting = true });
        var target = kind == DeletionTargetKind.Session ? DeletionTarget.Session(workspace, session) : DeletionTarget.Workspace(workspace);
        (await ScalarAsync("SELECT COUNT(*) FROM dbo.WorkItems WHERE TaskType = 5 AND WorkUnitKey = @name", target.WorkUnitKey)).ShouldBe(1);
    }

    /// <summary>
    /// Spec §13.1's crash after a partial purge. The first worker purges one row at a time until the session's conclusion is
    /// gone, then dies: its lease expires and a worker in a new <see cref="SqlMemoryStore"/> over the same database (so no
    /// in-process state survives) resumes the same item to the end. Ordinary writes are rejected throughout, and the
    /// conclusion citing the one the first worker removed ends tombstoned.
    /// </summary>
    [Fact]
    public async Task CrashAfterPartialPurge_ResumesAndCompletes()
    {
        var clock = new FakeTimeProvider(Start);
        var crashed = Database.CreateStore(clock);
        var (workspace, session) = await NewSessionAsync(crashed);
        await crashed.Sessions.AddPeersAsync(workspace, session, Members("alice", "bob"), Ct);
        var messages = await crashed.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend(
                [.. Enumerable.Range(0, 8).Select(i => Msg($"m{i}"))],
                Chunks: [.. Enumerable.Range(0, 8).Select(i => (IReadOnlyList<string>)[$"m{i} a", $"m{i} b"])],
                Summary: new SummaryIntent(8, 1000)),
            Ct);
        var summary = (await AcquireAsync(crashed, WorkTaskType.Summary)).ShouldNotBeNull();
        await crashed.Summaries.SaveAsync(summary, workspace, session, [new NewSummary(SummaryKind.Short, "s", messages[^1].PublicId, 8, 1)], Ct);
        var stated = (await crashed.Conclusions.InsertAsync(workspace, [Conclusion("bob likes tea", session)], Ct)).Single();
        var derived = (await crashed.Conclusions.InsertAsync(
            workspace, [Conclusion("bob enjoys hot drinks", null, ConclusionLevel.Deductive, stated.PublicId)], Ct)).Single();
        var target = DeletionTarget.Session(workspace, session);
        var acceptance = await crashed.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var first = (await AcquireAsync(crashed, WorkTaskType.Deletion)).ShouldNotBeNull();
        await crashed.Deletion.CancelOtherWorkAsync(first, target, Ct);

        for (var batch = 0; await crashed.Conclusions.GetAsync(workspace, stated.PublicId, Ct) is not null; batch++)
        {
            batch.ShouldBeLessThan(40);
            (await crashed.Deletion.PurgeBatchAsync(first, target, 1, Ct)).ShouldBeTrue();
            await ShouldRejectWritesAsync(crashed, workspace, session);
        }

        (await crashed.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(8, "the crash is mid-purge");
        clock.Advance(Lease + TimeSpan.FromSeconds(1));

        var resumed = Database.CreateStore(clock);
        var second = (await resumed.WorkQueue.TryAcquireAsync(
            "worker-2", new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Deletion] = WorkBatchPolicy.Immediate }, Lease, Ct))
            .ShouldNotBeNull();
        second.Items.Single().Id.ShouldBe(acceptance.JobId);
        await Should.ThrowAsync<LeaseLostException>(() => resumed.Deletion.PurgeBatchAsync(first, target, 1, Ct));
        await resumed.Deletion.CancelOtherWorkAsync(second, target, Ct);
        var batches = 0;
        while (await resumed.Deletion.PurgeBatchAsync(second, target, 3, Ct))
        {
            batches++;
            batches.ShouldBeLessThan(40);
            await ShouldRejectWritesAsync(resumed, workspace, session);
        }

        await ShouldRejectWritesAsync(resumed, workspace, session);
        await resumed.Deletion.RemoveAsync(second, target, Ct);

        (await resumed.Sessions.GetAsync(workspace, session, Ct)).ShouldBeNull();
        (await resumed.Conclusions.GetAsync(workspace, derived.PublicId, Ct)).ShouldBeNull("its only source was removed by the first worker");
        (await ScalarAsync("SELECT COUNT(*) FROM dbo.Conclusions WHERE PublicId = @name AND DeletedAt IS NOT NULL", derived.PublicId)).ShouldBe(1);
        (await ScalarAsync("SELECT COUNT(*) FROM dbo.Messages AS m JOIN dbo.Workspaces AS w ON w.Id = m.WorkspaceId WHERE w.Name = @name", workspace))
            .ShouldBe(0);
        var job = (await resumed.Deletion.ListJobsAsync(workspace, new PageRequest(), Ct)).Items.Single();
        (job.Status, job.Attempts).ShouldBe((JobStatus.Completed, 2));
    }

    /// <summary>
    /// The job's cancel step fences every other lease on the session's units (a derivation and a summary in flight), whose
    /// late commits then write nothing; a lease on another session's unit and the job's own lease are untouched.
    /// </summary>
    [Fact]
    public async Task OtherLeasesFenced_ByCancel()
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var (workspace, session) = await NewSessionAsync(store);
        var otherSession = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, otherSession, null, null, null, Ct);
        foreach (var name in new[] { session, otherSession })
        {
            await store.Sessions.AddPeersAsync(
                workspace, name, new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true) }, Ct);
        }

        var messages = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend(
                [.. Enumerable.Range(0, 20).Select(i => Msg($"m{i}"))],
                Derive: [.. Enumerable.Range(0, 20).Select(_ => DeriveIntent.Derive)],
                Summary: new SummaryIntent(20, 60)),
            Ct);
        await store.Messages.AppendAsync(workspace, otherSession, [Msg("elsewhere")], null, [DeriveIntent.Derive], Ct);
        var derivation = (await AcquireAsync(store, WorkTaskType.Representation)).ShouldNotBeNull();
        derivation.Items[0].SessionName.ShouldBe(session);
        var elsewhere = (await AcquireAsync(store, WorkTaskType.Representation)).ShouldNotBeNull();
        elsewhere.Items[0].SessionName.ShouldBe(otherSession);
        var summary = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var own = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();

        await store.Deletion.CancelOtherWorkAsync(own, DeletionTarget.Session(workspace, session), Ct);

        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(derivation, Lease, Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(summary, Lease, Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.Conclusions.CommitDerivationAsync(
            derivation,
            new DerivationChanges(workspace, [Conclusion("bob says hello", session)], [], []),
            Ct));
        await Should.ThrowAsync<LeaseLostException>(() => store.Summaries.SaveAsync(
            summary, workspace, session, [new NewSummary(SummaryKind.Short, "late", messages[19].PublicId, 20, 1)], Ct));
        await store.WorkQueue.RenewAsync(elsewhere, Lease, Ct);
        await store.WorkQueue.RenewAsync(own, Lease, Ct);
        (await ScalarAsync(
                "SELECT COUNT(*) FROM dbo.WorkItems AS i JOIN dbo.Sessions AS s ON s.Id = i.SessionId WHERE s.Name = @name AND i.Status IN (0, 1) AND i.TaskType <> 5",
                session))
            .ShouldBe(0);
        (await ScalarAsync(
                "SELECT COUNT(*) FROM dbo.WorkItems AS i JOIN dbo.Sessions AS s ON s.Id = i.SessionId WHERE s.Name = @name AND i.Status IN (0, 1)",
                otherSession))
            .ShouldBe(1);
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Total.ShouldBe(0);
    }

    /// <summary>
    /// The deadlock the lock order rules out: a worker commit holds its lease row and waits for the workspace row (here
    /// behind an exclusive lock, with a workspace acceptance queued too), while the deletion job's cancel step waits for that
    /// lease row. The cancel step must hold no workspace or session lock while it waits; then everything completes.
    /// </summary>
    [Fact]
    public async Task CancelOtherWork_TakesLeasesBeforeParentLocks()
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(
            workspace, session, new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true) }, Ct);
        await store.Messages.AppendAsync(workspace, session, [Msg("hello")], null, [DeriveIntent.Derive], Ct);
        var derivation = (await AcquireAsync(store, WorkTaskType.Representation)).ShouldNotBeNull();
        await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var own = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();

        await using var blocker = await OpenAsync();
        await using var blocking = (SqlTransaction)await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            blocking,
            "UPDATE dbo.Workspaces SET LifecycleVersion = LifecycleVersion WHERE Name = @workspace",
            ("@workspace", workspace));
        var commit = store.Conclusions.CommitDerivationAsync(
            derivation, new DerivationChanges(workspace, [Conclusion("bob says hello", session)], [], []), Ct);
        await WaitForLockWaitersAsync(1);
        var cancel = store.Deletion.CancelOtherWorkAsync(own, DeletionTarget.Session(workspace, session), Ct);
        await WaitForLockWaitersAsync(2);
        var acceptance = store.Deletion.BeginDeleteWorkspaceAsync(workspace, Ct);
        await WaitForLockWaitersAsync(3);

        (await ScalarAsync(
                """
                SELECT COUNT(*) FROM sys.dm_tran_locks AS held
                JOIN sys.partitions AS p ON p.hobt_id = held.resource_associated_entity_id
                WHERE held.resource_database_id = DB_ID() AND held.request_status = 'GRANT'
                  AND p.object_id IN (OBJECT_ID(N'dbo.Workspaces'), OBJECT_ID(N'dbo.Sessions'))
                  AND held.request_session_id IN (
                      SELECT w.request_session_id FROM sys.dm_tran_locks AS w
                      JOIN sys.partitions AS wp ON wp.hobt_id = w.resource_associated_entity_id
                      WHERE w.resource_database_id = DB_ID() AND w.request_status = 'WAIT'
                        AND wp.object_id = OBJECT_ID(N'dbo.WorkUnitLeases'))
                """,
                workspace))
            .ShouldBe(0, "the cancel step waits on a lease row holding no parent row");
        await blocking.RollbackAsync();

        await commit;
        await cancel;
        (await acceptance).AlreadyDeleting.ShouldBeFalse();
        (await ItemOutcomeAsync(derivation.Items.ShouldHaveSingleItem().Id)).ShouldBe("2:resource_deleted");
        (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Total.ShouldBe(0);
        await store.WorkQueue.RenewAsync(own, Lease, Ct);
    }

    [Fact]
    public async Task NameReusable_AfterRemove()
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice"), Ct);
        await store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("one"), Msg("two")], Chunks: [["one"], ["two"]]), Ct);
        await store.Conclusions.InsertAsync(workspace, [Conclusion("bob counts", session)], Ct);
        var target = DeletionTarget.Session(workspace, session);
        var removed = await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        var lease = (await AcquireAsync(store, WorkTaskType.Deletion)).ShouldNotBeNull();
        await store.Deletion.CancelOtherWorkAsync(lease, target, Ct);
        while (await store.Deletion.PurgeBatchAsync(lease, target, 2, Ct))
        {
        }

        await store.Deletion.RemoveAsync(lease, target, Ct);

        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(lease, Lease, Ct));
        var reused = await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        reused.State.ShouldBe(LifecycleState.Active);
        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Total.ShouldBe(0);
        var appended = await store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("fresh")]), Ct);
        appended.Single().Seq.ShouldBe(1);
        var again = await store.Deletion.BeginDeleteSessionAsync(workspace, session, Ct);
        again.JobId.ShouldNotBe(removed.JobId);
        var jobs = (await store.Deletion.ListJobsAsync(workspace, new PageRequest(), Ct)).Items;
        jobs.Select(job => (job.Id, job.Target, job.Status)).ShouldBe(
        [
            (removed.JobId, target, JobStatus.Completed),
            (again.JobId, target, JobStatus.Pending),
        ]);
    }

    private static async Task ShouldRejectWritesAsync(SqlMemoryStore store, string workspace, string session)
    {
        (await Should.ThrowAsync<ConflictException>(
                () => store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("late")]), Ct)))
            .Message.ShouldBe("session is being deleted");
        (await Should.ThrowAsync<ConflictException>(() => store.Sessions.AddPeersAsync(workspace, session, Members("carol"), Ct)))
            .Message.ShouldBe("session is being deleted");
    }

    private static NewConclusion Conclusion(
        string content, string? session, ConclusionLevel level = ConclusionLevel.Explicit, params string[] sources) =>
        new("alice", "bob", session, level, content, null, sources, null);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static NewMessage Msg(string content) => new("bob", content, 1, null, null);

    private static Dictionary<string, SessionPeerConfig> Members(params string[] names) =>
        names.ToDictionary(name => name, _ => new SessionPeerConfig());

    private static async Task<(string Workspace, string Session)> NewSessionAsync(IMemoryStore store)
    {
        var workspace = Unique("ws");
        var session = Unique("sess");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        return (workspace, session);
    }

    private static Task<WorkLease?> AcquireAsync(IMemoryStore store, WorkTaskType type) =>
        store.WorkQueue.TryAcquireAsync(
            "worker-1", new Dictionary<WorkTaskType, WorkBatchPolicy> { [type] = WorkBatchPolicy.Immediate }, Lease, Ct);

    /// <summary>A plain store, and one over the same database whose deletion acceptance pauses at <paramref name="pause"/>.</summary>
    private (IMemoryStore Store, IMemoryStore Paused) Stores(Pause pause)
    {
        var clock = new FakeTimeProvider(Start);
        var options = new SqlServerOptions { ConnectionString = Database.ConnectionString, BeforeLifecycleTransition = pause.WaitAsync };
        return (Database.CreateStore(clock), SqlTestDatabase.CreateStore(options, Database.Gate, clock));
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> requests in this database wait on a lock: the racing side has reached
    /// the paused side's locks. The database is this class's own, and its tests do not run in parallel.
    /// </summary>
    private async Task WaitForLockWaitersAsync(int count)
    {
        await using var connection = await OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND wait_type LIKE N'LCK[_]M[_]%'", connection);
            if ((int)(await command.ExecuteScalarAsync(timeout.Token))! >= count)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private Task<int> DeletionItemsAsync(string workspace) =>
        ScalarAsync("SELECT COUNT(*) FROM dbo.WorkItems AS i JOIN dbo.Workspaces AS w ON w.Id = i.WorkspaceId WHERE i.TaskType = 5 AND w.Name = @name", workspace);

    private async Task<string> ItemOutcomeAsync(long id)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("SELECT CONCAT(Status, ':', Error) FROM dbo.WorkItems WHERE Id = @id", connection);
        command.Parameters.AddWithValue("@id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> ScalarAsync(string sql, string name)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@name", name);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, string Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>A one-shot pause: the first caller of <see cref="WaitAsync"/> signals <see cref="Reached"/> and waits for <see cref="Release"/>.</summary>
    private sealed class Pause
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public async Task WaitAsync(CancellationToken ct)
        {
            if (_reached.TrySetResult())
            {
                await _released.Task.WaitAsync(TimeSpan.FromMinutes(1), ct);
            }
        }

        public void Release() => _released.TrySetResult();
    }
}
