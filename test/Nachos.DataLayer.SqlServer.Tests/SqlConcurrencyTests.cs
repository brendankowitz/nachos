using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// SQL-specific concurrency guarantees, checked against the rows themselves: get-or-create races leave exactly one row
/// (insert, catch 2627/2601, re-read), and parallel appends allocate a gap-free, unique <c>Seq</c>.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlConcurrencyTests(SqlServerFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

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
    public async Task PeersAndMemberships_GetOrCreate_IsIdempotentUnderConcurrency_OneRowEach()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "concurrency");
        var store = database.CreateStore(TimeProvider.System);
        var workspace = Unique("ws");
        await RunConcurrentlyAsync(16, _ => store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct));

        // Every caller creates the same session with overlapping member sets, so peers and memberships race too.
        var results = await RunConcurrentlyAsync<object>(48, async i =>
        {
            if (i % 3 == 0)
            {
                return await store.Peers.GetOrCreateAsync(workspace, $"p{i % 4}", null, null, Ct);
            }

            var members = new Dictionary<string, SessionPeerConfig>
            {
                [$"p{i % 4}"] = new(true, false),
                [$"p{(i + 1) % 4}"] = new(false, true),
            };
            return await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, members, Ct);
        });
        results.OfType<SessionRecord>().Select(s => s.CreatedAt).Distinct().Count().ShouldBe(1);

        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.Workspaces WHERE Name = @w", workspace)).ShouldBe(1);
        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.Peers p JOIN dbo.Workspaces w ON w.Id = p.WorkspaceId WHERE w.Name = @w", workspace)).ShouldBe(4);
        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.Sessions s JOIN dbo.Workspaces w ON w.Id = s.WorkspaceId WHERE w.Name = @w", workspace)).ShouldBe(1);
        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.SessionPeers sp JOIN dbo.Workspaces w ON w.Id = sp.WorkspaceId WHERE w.Name = @w AND sp.LeftAt IS NULL", workspace)).ShouldBe(4);
        (await store.Sessions.ListPeersAsync(workspace, "s", new PageRequest(), Ct)).Total.ShouldBe(4);
    }

    [Fact]
    public async Task Append_InParallel_AllocatesGapFreeUniqueSeq()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "concurrency");
        var store = database.CreateStore(TimeProvider.System);
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, null, Ct);

        // 16 writers x 5 appends x 3 messages, from 8 senders that do not exist yet (so sender creation races as well).
        var batches = await RunConcurrentlyAsync(16, async writer =>
        {
            var appended = new List<MessageRecord>();
            for (var round = 0; round < 5; round++)
            {
                appended.AddRange(await store.Messages.AppendAsync(
                    workspace,
                    "s",
                    [.. Enumerable.Range(0, 3).Select(i => new NewMessage($"sender{(writer + i) % 8}", $"w{writer} r{round} m{i}", 1, null, null))],
                    null,
                    Ct));
            }

            return appended;
        });

        const int expected = 16 * 5 * 3;
        var returned = batches.SelectMany(b => b).Select(m => m.Seq).Order().ToList();
        returned.ShouldBe(Enumerable.Range(1, expected).Select(i => (long)i));

        // Within one append the Seq values are contiguous and follow message order.
        foreach (var batch in batches.SelectMany(b => b.Chunk(3)))
        {
            batch.Select(m => m.Seq).ShouldBe([batch[0].Seq, batch[0].Seq + 1, batch[0].Seq + 2]);
        }

        var listed = new List<long>();
        await foreach (var message in ((Func<PageRequest, CancellationToken, Task<Page<MessageRecord>>>)((page, ct) =>
            store.Messages.ListAsync(workspace, "s", null, page, ct))).EnumerateAsync(100))
        {
            listed.Add(message.Seq);
        }

        listed.ShouldBe(Enumerable.Range(1, expected).Select(i => (long)i));
        (await CountAsync(database, "SELECT s.NextMessageSeq FROM dbo.Sessions s JOIN dbo.Workspaces w ON w.Id = s.WorkspaceId WHERE w.Name = @w", workspace))
            .ShouldBe(expected + 1);
        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.Peers p JOIN dbo.Workspaces w ON w.Id = p.WorkspaceId WHERE w.Name = @w", workspace)).ShouldBe(8);
        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.SessionPeers sp JOIN dbo.Workspaces w ON w.Id = sp.WorkspaceId WHERE w.Name = @w", workspace)).ShouldBe(8);
    }

    [Fact]
    public async Task Append_SameIdempotencyKeyAcrossSessions_OneWinner()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "concurrency");
        var store = database.CreateStore(TimeProvider.System);
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        for (var i = 0; i < 8; i++)
        {
            await store.Sessions.GetOrCreateAsync(workspace, $"s{i}", null, null, null, Ct);
        }

        // Keys are scoped per workspace, so appends to different sessions (which do not share a session row lock) race
        // on the key itself.
        var key = Unique("key");
        var outcomes = await RunConcurrentlyAsync<Exception?>(8, async i =>
        {
            try
            {
                await store.Messages.AppendAsync(
                    workspace,
                    $"s{i}",
                    [new NewMessage("alice", "hi", 1, null, null)],
                    new IdempotencyWrite(key, new string('a', 64), 201, m => m[0].PublicId, TimeSpan.FromMinutes(5)),
                    Ct);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        outcomes.Count(e => e is null).ShouldBe(1);
        outcomes.Where(e => e is not null).ShouldAllBe(e => e is IdempotencyDuplicateException);
        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.Messages m JOIN dbo.Workspaces w ON w.Id = m.WorkspaceId WHERE w.Name = @w", workspace)).ShouldBe(1);
        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.IdempotencyRecords r JOIN dbo.Workspaces w ON w.Id = r.WorkspaceId WHERE w.Name = @w", workspace)).ShouldBe(1);
    }

    [Fact]
    public async Task Append_RacingMembershipChanges_NeverDeadlocks()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "concurrency");
        var store = database.CreateStore(TimeProvider.System);
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, null, Ct);

        // Created in reverse name order, so peer id order (row scans) is the opposite of name order (appends). Every
        // membership writer takes the session row first (see Upserts), so these races must all succeed. This is a race
        // smoke test: it did not reproduce a deadlock with that lock removed from RemovePeers either, so the lock order
        // is argued in Upserts rather than proven here.
        string[] names = ["z", "y", "x", "w"];
        foreach (var name in names)
        {
            await store.Peers.GetOrCreateAsync(workspace, name, null, null, Ct);
        }

        await RunConcurrentlyAsync(8, async writer =>
        {
            for (var round = 0; round < 15; round++)
            {
                switch ((writer + round) % 4)
                {
                    case 0:
                        await store.Messages.AppendAsync(
                            workspace, "s", [.. names.Select(name => new NewMessage(name, $"{writer}/{round}", 1, null, null))], null, Ct);
                        break;
                    case 1:
                        await store.Sessions.RemovePeersAsync(workspace, "s", names, Ct);
                        break;
                    case 2:
                        await store.Sessions.SetPeersAsync(workspace, "s", new Dictionary<string, SessionPeerConfig> { ["w"] = new(true, true) }, Ct);
                        break;
                    default:
                        await store.Sessions.AddPeersAsync(workspace, "s", names.ToDictionary(name => name, _ => new SessionPeerConfig(false, false)), Ct);
                        break;
                }
            }

            return writer;
        });

        (await CountAsync(database, "SELECT COUNT(*) FROM dbo.SessionPeers sp JOIN dbo.Workspaces w ON w.Id = sp.WorkspaceId WHERE w.Name = @w", workspace)).ShouldBe(4);
    }

    private static async Task<long> CountAsync(SqlTestDatabase database, string sql, string workspace)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@w", workspace);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
