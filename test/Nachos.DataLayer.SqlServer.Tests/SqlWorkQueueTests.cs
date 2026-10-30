using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Filtering;
using Nachos.DataLayer.SqlServer.Filtering;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.InMemory;
using Nachos.Testing;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// SQL-specific guarantees of the fenced queue and the transactional enqueue, under real concurrency with read committed
/// snapshot ON: one owner per unit, a single winner of an expiry race, no deadlocks between the writers' lock orders,
/// enqueue atomic with the append, and vector ranking identical to the in-memory provider's.
/// </summary>
[Collection(M2StoreContracts.Name)]
public sealed class SqlWorkQueueTests(SqlServerFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> Immediate =
        new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Representation] = WorkBatchPolicy.Immediate };

    private static readonly IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> Batching =
        new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Representation] = new(0, TimeSpan.Zero, 1000) };

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<SqlTestDatabase> DatabaseAsync()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "m2-queue-sql");
        await database.ResetQueueAsync();
        return database;
    }

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

    private static async Task<(string Workspace, string Session)> ArrangeAsync(SqlMemoryStore store)
    {
        var workspace = Unique("ws");
        var session = Unique("sess");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        return (workspace, session);
    }

    private static Task<IReadOnlyList<MessageRecord>> AppendAsync(SqlMemoryStore store, string workspace, string session, string sender, int count = 1) =>
        store.Messages.AppendAsync(
            workspace,
            session,
            [.. Enumerable.Range(0, count).Select(i => new NewMessage(sender, $"{sender} says {i}", 3, null, null))],
            null,
            [.. Enumerable.Repeat(DeriveIntent.Derive, count)],
            Ct);

    /// <summary>One self-observed conclusion per claimed item, so a double commit would show as a duplicate row.</summary>
    private static DerivationChanges OneConclusionPerItem(string workspace, WorkLease lease) =>
        new(
            workspace,
            [
                .. lease.Items.Select(item =>
                {
                    var sender = item.WorkUnitKey[(item.WorkUnitKey.LastIndexOf(':') + 1)..];
                    return new NewConclusion(sender, sender, item.SessionName, ConclusionLevel.Explicit, $"item {item.Id}", null, [], null);
                }),
            ],
            [],
            []);

    private static async Task<T> ScalarAsync<T>(SqlTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    // ---------------------------------------------------------------- one owner per unit

    [Fact]
    public async Task ConcurrentClaimers_NeverShareAUnit()
    {
        var database = await DatabaseAsync();
        (await ScalarAsync<bool>(database, "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()")).ShouldBeTrue();
        var store = database.CreateStore(TimeProvider.System);
        var (workspace, session) = await ArrangeAsync(store);
        var other = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, other, null, null, null, Ct);

        // 12 units (6 senders in 2 sessions) of 3 items each.
        var total = 0;
        foreach (var target in new[] { session, other })
        {
            for (var sender = 0; sender < 6; sender++)
            {
                total += (await AppendAsync(store, workspace, target, $"peer{sender}", count: 3)).Count;
            }
        }

        var committed = new ConcurrentBag<long>();
        var acquisitions = new ConcurrentBag<(string Unit, long Token)>();
        var lost = 0;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        await RunConcurrentlyAsync(16, async claimer =>
        {
            var random = new Random(claimer);
            var leases = 0;
            while (committed.Count < total && DateTime.UtcNow < deadline)
            {
                var lease = await store.WorkQueue.TryAcquireAsync($"claimer-{claimer}", Immediate, TimeSpan.FromMilliseconds(250), Ct);
                if (lease is null)
                {
                    await Task.Delay(5, Ct);
                    continue;
                }

                acquisitions.Add((lease.WorkUnitKey, lease.FencingToken));

                // Half the claimers outlive the expiry of their first lease, and every fourth lease after that does, so
                // other claimers reclaim units while they are still being worked.
                var outlivesLease = leases++ == 0 ? claimer % 2 == 0 : random.Next(4) == 0;
                await Task.Delay(outlivesLease ? 400 : random.Next(20), Ct);
                try
                {
                    await store.Conclusions.CommitDerivationAsync(lease, OneConclusionPerItem(workspace, lease), Ct);
                    foreach (var item in lease.Items)
                    {
                        committed.Add(item.Id);
                    }
                }
                catch (LeaseLostException)
                {
                    Interlocked.Increment(ref lost);
                }
            }

            return 0;
        });

        // Every item was committed exactly once: a stale owner never wrote after a reclaim.
        committed.Count.ShouldBe(total);
        committed.Distinct().Count().ShouldBe(total);
        (await ScalarAsync<int>(database, "SELECT COUNT(*) FROM dbo.Conclusions c JOIN dbo.Workspaces w ON w.Id = c.WorkspaceId WHERE w.Name = @w", ("@w", workspace)))
            .ShouldBe(total);
        (await ScalarAsync<int>(database, "SELECT COUNT(DISTINCT c.Content) FROM dbo.Conclusions c JOIN dbo.Workspaces w ON w.Id = c.WorkspaceId WHERE w.Name = @w", ("@w", workspace)))
            .ShouldBe(total);
        (await ScalarAsync<int>(database, "SELECT COUNT(*) FROM dbo.WorkItems WHERE Status <> 2")).ShouldBe(0);

        // No two leases on a unit ever shared a token, and reclaims really happened.
        acquisitions.GroupBy(a => a.Unit).ShouldAllBe(unit => unit.Select(a => a.Token).Distinct().Count() == unit.Count());
        acquisitions.GroupBy(a => a.Unit).ShouldContain(unit => unit.Count() > 1);
        lost.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ExpiryReclaimRace_SingleWinner()
    {
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(Start);
        var store = database.CreateStore(clock);
        var (workspace, session) = await ArrangeAsync(store);
        await AppendAsync(store, workspace, session, "bob");

        var expired = (await store.WorkQueue.TryAcquireAsync("first", Immediate, TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();
        var singleton = (await store.WorkQueue.TryLeaseAsync(WorkUnitKeys.Reconciler, "first", TimeSpan.FromMinutes(1), Ct)).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromMinutes(2));

        var claims = await RunConcurrentlyAsync(16, i => database.CreateStore(clock).WorkQueue.TryAcquireAsync($"c{i}", Immediate, TimeSpan.FromMinutes(1), Ct));
        var winner = claims.OfType<WorkLease>().ShouldHaveSingleItem();
        winner.WorkUnitKey.ShouldBe(expired.WorkUnitKey);
        winner.FencingToken.ShouldBeGreaterThan(expired.FencingToken);
        winner.Items.ShouldHaveSingleItem().Attempts.ShouldBe(2);
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.CompleteAsync(expired, Ct));

        var leases = await RunConcurrentlyAsync(16, i => database.CreateStore(clock).WorkQueue.TryLeaseAsync(WorkUnitKeys.Reconciler, $"c{i}", TimeSpan.FromMinutes(1), Ct));
        leases.OfType<WorkLease>().ShouldHaveSingleItem().FencingToken.ShouldBeGreaterThan(singleton.FencingToken);
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(singleton, TimeSpan.FromMinutes(1), Ct));

        await store.WorkQueue.CompleteAsync(winner, Ct);
        (await store.WorkQueue.GetStatusAsync(workspace, null, null, null, Ct)).Totals.ShouldBe(new QueueCounts(0, 0, 1, 0));
    }

    // ---------------------------------------------------------------- lock order

    /// <summary>
    /// Appenders (session, messages, work items), claimers (work items, leases) and committers (lease, work items,
    /// collections, conclusions, with corroborations of shared rows in arbitrary id order) run together. A deadlock in an
    /// append or a commit fails the run with error 1205; a claim that loses one is skipped (and logged), so the server's
    /// deadlock counter is read before and after and must not move. Nothing expires, so no lease may be lost either.
    /// </summary>
    [Fact]
    public async Task ParallelWorkers_NoDeadlock()
    {
        var database = await DatabaseAsync();
        var deadlocksBefore = await DeadlockCountAsync(database);
        var store = database.CreateStore(TimeProvider.System);
        var (workspace, session) = await ArrangeAsync(store);
        var sessions = new[] { session, Unique("sess"), Unique("sess") };
        foreach (var name in sessions.Skip(1))
        {
            await store.Sessions.GetOrCreateAsync(workspace, name, null, null, null, Ct);
        }

        var senders = new[] { "alice", "bob", "carol" };
        foreach (var sender in senders)
        {
            await store.Peers.GetOrCreateAsync(workspace, sender, null, null, Ct);
        }

        // A shared pool of conclusions that every committer corroborates, in random order.
        var shared = await store.Conclusions.InsertAsync(
            workspace,
            [.. senders.SelectMany(sender => Enumerable.Range(0, 4).Select(i => new NewConclusion(sender, sender, null, ConclusionLevel.Explicit, $"{sender} {i}", null, [], null)))],
            Ct);

        const int Appenders = 6;
        const int AppendsEach = 15;
        var appending = Appenders;
        var completed = 0;
        var failed = 0;
        var appendTasks = RunConcurrentlyAsync(Appenders, async appender =>
        {
            for (var i = 0; i < AppendsEach; i++)
            {
                await store.Messages.AppendAsync(
                    workspace,
                    sessions[(appender + i) % sessions.Length],
                    [new NewMessage(senders[i % senders.Length], "hi", 2, null, null), new NewMessage(senders[(i + 1) % senders.Length], "hey", 2, null, null)],
                    null,
                    [DeriveIntent.Derive, DeriveIntent.Derive],
                    Ct);
            }

            Interlocked.Decrement(ref appending);
            return 0;
        });

        var workerTasks = RunConcurrentlyAsync(8, async worker =>
        {
            var random = new Random(worker);
            var hasFailed = false;
            while (true)
            {
                var lease = await store.WorkQueue.TryAcquireAsync($"worker-{worker}", Batching, TimeSpan.FromMinutes(1), Ct);
                if (lease is null)
                {
                    if (Volatile.Read(ref appending) == 0
                        && (await store.WorkQueue.GetStatusAsync(workspace, null, null, null, Ct)).Totals.Pending == 0)
                    {
                        return 0;
                    }

                    await Task.Delay(5, Ct);
                    continue;
                }

                await store.WorkQueue.RenewAsync(lease, TimeSpan.FromMinutes(1), Ct);
                // Each worker fails its first lease, so failed items are retried among the others.
                if (!hasFailed)
                {
                    hasFailed = true;
                    await store.WorkQueue.FailAsync(lease, "transient", TimeSpan.Zero, 100, Ct);
                    Interlocked.Increment(ref failed);
                    continue;
                }

                var corroborations = shared.OrderBy(_ => random.Next()).Take(5).Select(c => c.PublicId).ToList();
                var replacement = shared[random.Next(shared.Count)];
                await store.Conclusions.CommitDerivationAsync(
                    lease,
                    OneConclusionPerItem(workspace, lease) with
                    {
                        Corroborations = corroborations,
                        Replacements = [new ConclusionReplacement(replacement.PublicId, $"{replacement.Content} (seen)", null)],
                    },
                    Ct);
                Interlocked.Increment(ref completed);
            }
        });

        await Task.WhenAll(appendTasks, workerTasks);

        var status = await store.WorkQueue.GetStatusAsync(workspace, null, null, null, Ct);
        status.Totals.ShouldBe(new QueueCounts(0, 0, Appenders * AppendsEach * 2, 0));
        completed.ShouldBeGreaterThan(0);
        failed.ShouldBeGreaterThan(0);
        (await DeadlockCountAsync(database) - deadlocksBefore).ShouldBe(0, "deadlocks during the run");
    }

    /// <summary>
    /// API inserts and derivations that create the same new collections, listed in opposite orders, run together. Each
    /// creates the collections in key order, so their unique-key inserts cannot deadlock (in conclusion order they did).
    /// </summary>
    [Fact]
    public async Task NewCollections_InOppositeOrders_NoDeadlock()
    {
        var database = await DatabaseAsync();
        var store = database.CreateStore(TimeProvider.System);
        var deadlocksBefore = await DeadlockCountAsync(database);
        var peers = Enumerable.Range(0, 4).Select(i => $"p{i}").ToList();
        var pairs = peers.SelectMany(observer => peers.Select(observed => (observer, observed))).ToList();

        for (var round = 0; round < 10; round++)
        {
            var workspace = Unique("ws");
            await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
            foreach (var peer in peers)
            {
                await store.Peers.GetOrCreateAsync(workspace, peer, null, null, Ct);
            }

            await RunConcurrentlyAsync(8, async writer =>
            {
                var ordered = writer % 2 == 0 ? pairs : [.. Enumerable.Reverse(pairs)];
                return await store.Conclusions.InsertAsync(
                    workspace,
                    [.. ordered.Select(pair => new NewConclusion(pair.observer, pair.observed, null, ConclusionLevel.Explicit, $"w{writer}", null, [], null))],
                    Ct);
            });

            (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(1, 100), Ct)).Total
                .ShouldBe(8 * pairs.Count);
        }

        (await DeadlockCountAsync(database) - deadlocksBefore).ShouldBe(0, "deadlocks during the run");
    }

    /// <summary>
    /// A conclusion id filter travels as one JSON parameter, however many ids it lists, so the Deriver's
    /// <c>PublicId in [...]</c> filter is not bounded by the 2000-parameter limit (and needs no cap on its batch size).
    /// </summary>
    [Fact]
    public async Task ConclusionIdFilter_ThousandsOfIds_UsesOneParameter()
    {
        var database = await DatabaseAsync();
        var store = database.CreateStore(TimeProvider.System);
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Peers.GetOrCreateAsync(workspace, "bob", null, null, Ct);
        var inserted = await store.Conclusions.InsertAsync(
            workspace, [new NewConclusion("bob", "bob", null, ConclusionLevel.Explicit, "fact", null, [], null)], Ct);
        var ids = new JsonArray([.. Enumerable.Range(0, 5000).Select(i => JsonValue.Create($"missing-{i}")), JsonValue.Create(inserted[0].PublicId)]);

        var filter = new FilterNode.Field(FilterColumns.PublicId, FilterOp.In, ids);
        SqlFilterCompiler.Compile(filter, ResourceKind.Conclusion, "c").Parameters.Count.ShouldBe(1);
        (await store.Conclusions.ListAsync(workspace, filter, ConclusionOrder.CreatedAt, new PageRequest(), Ct))
            .Items.ShouldHaveSingleItem().PublicId.ShouldBe(inserted[0].PublicId);
    }

    /// <summary>The server's cumulative deadlock count (the counter is named per second but counts since start-up).</summary>
    private static Task<long> DeadlockCountAsync(SqlTestDatabase database) =>
        ScalarAsync<long>(
            database,
            "SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name LIKE N'Number of Deadlocks/sec%' AND instance_name = N'_Total'");

    // ---------------------------------------------------------------- vectors

    [Fact]
    public async Task VectorNearest_MatchesInMemory()
    {
        var database = await DatabaseAsync();
        var sql = database.CreateStore(TimeProvider.System);
        var memory = new InMemoryMemoryStore(TimeProvider.System);
        var random = new Random(1536);
        float[] RandomVector() => [.. Enumerable.Range(0, VectorSpace.Dimensions).Select(_ => (float)((random.NextDouble() * 2) - 1))];

        var fixtures = Enumerable.Range(0, 40)
            .Select(i => new NewConclusion(
                i % 2 == 0 ? "alice" : "bob", "bob", null, i % 3 == 0 ? ConclusionLevel.Deductive : ConclusionLevel.Explicit, $"fact {i}", RandomVector(), [], null))
            .ToList();
        var queries = Enumerable.Range(0, 8).Select(_ => RandomVector()).ToList();
        var workspace = Unique("ws");
        foreach (var store in new IMemoryStore[] { sql, memory })
        {
            await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
            await store.Peers.GetOrCreateAsync(workspace, "alice", null, null, Ct);
            await store.Peers.GetOrCreateAsync(workspace, "bob", null, null, Ct);
            await store.Conclusions.InsertAsync(workspace, fixtures, Ct);
        }

        foreach (var query in queries)
        {
            var expected = await memory.Conclusions.QueryAsync(workspace, query, 10, null, null, Ct);
            var actual = await sql.Conclusions.QueryAsync(workspace, query, 10, null, null, Ct);
            actual.Select(m => m.Conclusion.Content).ShouldBe(expected.Select(m => m.Conclusion.Content));
            actual.Zip(expected).ShouldAllBe(pair => Math.Abs(pair.First.Distance - pair.Second.Distance) < 1e-5);

            var threshold = expected[4].Distance;
            (await sql.Conclusions.QueryAsync(workspace, query, 10, threshold + 1e-6, null, Ct)).Count.ShouldBe(5);

            foreach (var level in new[] { ConclusionLevel.Explicit, ConclusionLevel.Deductive })
            {
                var nearestExpected = (await memory.Conclusions.FindNearestAsync(workspace, "alice", "bob", level, query, 2, null, Ct)).ShouldNotBeNull();
                var nearestActual = (await sql.Conclusions.FindNearestAsync(workspace, "alice", "bob", level, query, 2, null, Ct)).ShouldNotBeNull();
                nearestActual.Conclusion.Content.ShouldBe(nearestExpected.Conclusion.Content);
                nearestActual.Distance.ShouldBe(nearestExpected.Distance, 1e-5);
            }
        }
    }

    // ---------------------------------------------------------------- enqueue atomicity

    /// <summary>
    /// While appends run, one snapshot statement repeatedly counts the session's messages and its work items: each append
    /// commits both or neither, so the counts never differ. Every item points at its message, and a rolled-back append
    /// leaves neither.
    /// </summary>
    [Fact]
    public async Task Append_WithDerive_ItemsCommitWithMessages()
    {
        var database = await DatabaseAsync();
        var store = database.CreateStore(TimeProvider.System);
        var (workspace, session) = await ArrangeAsync(store);
        const string Counts = """
            SELECT CONCAT(
                (SELECT COUNT(*) FROM dbo.Messages m JOIN dbo.Sessions s ON s.Id = m.SessionId WHERE s.Name = @s), ':',
                (SELECT COUNT(*) FROM dbo.WorkItems i JOIN dbo.Sessions s ON s.Id = i.SessionId WHERE s.Name = @s))
            """;

        var appending = 1;
        var reader = Task.Run(async () =>
        {
            var observations = 0;
            while (Volatile.Read(ref appending) == 1)
            {
                var counts = (await ScalarAsync<string>(database, Counts, ("@s", session))).Split(':');
                counts[1].ShouldBe(counts[0]);
                observations++;
            }

            return observations;
        });

        await RunConcurrentlyAsync(8, async writer =>
        {
            for (var i = 0; i < 10; i++)
            {
                await AppendAsync(store, workspace, session, $"writer{writer}", count: 2);
            }

            return 0;
        });
        Volatile.Write(ref appending, 0);
        (await reader).ShouldBeGreaterThan(0);

        (await ScalarAsync<int>(database, """
            SELECT COUNT(*) FROM dbo.WorkItems i
            JOIN dbo.Messages m ON m.Id = i.MessageId AND m.SessionId = i.SessionId AND m.WorkspaceId = i.WorkspaceId
            JOIN dbo.Sessions s ON s.Id = i.SessionId
            WHERE s.Name = @s AND i.Status = 0 AND i.Attempts = 0 AND i.TaskType = 1 AND i.TokenCount = m.TokenCount
            """, ("@s", session))).ShouldBe(160);

        await Should.ThrowAsync<InvalidOperationException>(() => store.Messages.AppendAsync(
            workspace,
            session,
            [new NewMessage("late", "x", 1, null, null)],
            new IdempotencyWrite("k", new string('a', 64), 201, _ => throw new InvalidOperationException("boom"), TimeSpan.FromMinutes(1)),
            [DeriveIntent.Derive],
            Ct));
        (await ScalarAsync<string>(database, Counts, ("@s", session))).ShouldBe("160:160");
    }
}
