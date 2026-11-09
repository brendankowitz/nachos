using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Schema;
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
