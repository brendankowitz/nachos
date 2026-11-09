using Microsoft.Data.SqlClient;
using Nachos.Abstractions.Domain;
using Nachos.Testing;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The chunk backfill (<c>AddChunksAsync</c>) under concurrent callers whose chunks overlap.</summary>
[Collection(M2StoreContracts.Name)]
public sealed class SqlEmbeddingBackfillTests(SqlServerFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Two callers backfill overlapping but different slices of the same messages, at once, round after round: one a
    /// leading block plus the message that sorts first, the other a middle block. Their key-range locks need not come in a
    /// common order, so a deadlock is possible; the losing batch is retried, so both calls complete, and the rows are
    /// exactly the union of the two slices.
    /// </summary>
    [Fact]
    public async Task OverlappingSlices_Concurrently_InsertTheUnion()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "embedding-backfill");
        var store = database.CreateStore(TimeProvider.System);
        var workspace = $"ws-{Guid.NewGuid():N}";
        var session = $"sess-{Guid.NewGuid():N}";
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        var messages = await store.Messages.AppendAsync(
            workspace, session, [.. Enumerable.Range(0, 60).Select(i => new NewMessage("bob", $"m{i}", 1, null, null))], null, Ct);
        var ordered = messages.Select(message => message.PublicId).Order(StringComparer.Ordinal).ToArray();
        string[] leading = [ordered[0], .. ordered[10..40]];
        var middle = ordered[20..50];

        const int Rounds = 10;
        for (var round = 0; round < Rounds; round++)
        {
            var index = round;
            List<MessageChunk> Slice(IEnumerable<string> ids) =>
                [.. ids.Select(id => new MessageChunk(workspace, session, id, index, $"{id} {index}"))];

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new[] { Slice(leading), Slice(middle) }
                .Select(slice => Task.Run(async () =>
                {
                    await gate.Task;
                    await store.Embeddings.AddChunksAsync(slice, Ct);
                }))
                .ToArray();
            gate.SetResult();
            await Task.WhenAll(calls);
        }

        var expected = leading.Union(middle, StringComparer.Ordinal)
            .SelectMany(id => Enumerable.Range(0, Rounds).Select(index => $"{id}:{index}"))
            .Order(StringComparer.Ordinal)
            .ToList();
        (await StoredChunksAsync(database, session)).ShouldBe(expected);
    }

    /// <summary>
    /// A batch chosen as a deadlock victim is retried. The deadlock is forced: a blocker at <c>DEADLOCK_PRIORITY HIGH</c>
    /// holds an uncommitted row of the chunk's key, the backfill (holding its S lock on the session) waits on it, then the
    /// blocker asks for the session row's exclusive lock. The backfill is rolled back, retries once the blocker is gone,
    /// and inserts the chunk.
    /// </summary>
    [Fact]
    public async Task DeadlockVictimBatch_IsRetried()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "embedding-backfill");
        var store = database.CreateStore(TimeProvider.System);
        var workspace = $"ws-{Guid.NewGuid():N}";
        var session = $"sess-{Guid.NewGuid():N}";
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        var message = (await store.Messages.AppendAsync(workspace, session, [new NewMessage("bob", "hello", 1, null, null)], null, Ct)).Single();
        var deadlocksBefore = await ScalarAsync<long>(database, DeadlockCount);

        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync(Ct);
        await using var blocking = (SqlTransaction)await blocker.BeginTransactionAsync(Ct);
        await ExecuteAsync(
            blocker,
            blocking,
            """
            SET DEADLOCK_PRIORITY HIGH;
            INSERT dbo.MessageEmbeddings (WorkspaceId, MessageId, SessionId, ChunkIndex, Content, SyncState, CreatedAt)
            SELECT m.WorkspaceId, m.Id, m.SessionId, 0, N'blocking', 0, SYSDATETIMEOFFSET() FROM dbo.Messages AS m WHERE m.PublicId = @id
            """,
            message.PublicId);
        var backfill = store.Embeddings.AddChunksAsync([new MessageChunk(workspace, session, message.PublicId, 0, "kept")], Ct);
        await WaitForLockWaitersAsync(database, 1);
        await ExecuteAsync(
            blocker,
            blocking,
            "UPDATE s SET LifecycleVersion = LifecycleVersion FROM dbo.Sessions AS s JOIN dbo.Messages AS m ON m.SessionId = s.Id WHERE m.PublicId = @id",
            message.PublicId);
        await blocking.RollbackAsync(Ct);

        await backfill;
        (await ScalarAsync<long>(database, DeadlockCount)).ShouldBeGreaterThan(deadlocksBefore);
        (await StoredChunksAsync(database, session)).ShouldBe([$"{message.PublicId}:0"]);
        (await ScalarAsync<string>(
                database,
                "SELECT e.Content FROM dbo.MessageEmbeddings AS e JOIN dbo.Messages AS m ON m.Id = e.MessageId WHERE m.PublicId = @id",
                message.PublicId))
            .ShouldBe("kept");
    }

    private const string DeadlockCount =
        "SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name LIKE N'Number of Deadlocks/sec%' AND instance_name = N'_Total'";

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, string id)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(SqlTestDatabase database, string sql, string? id = null)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        if (id is not null)
        {
            command.Parameters.AddWithValue("@id", id);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Waits until at least <paramref name="count"/> requests in the database wait on a lock.</summary>
    private static async Task WaitForLockWaitersAsync(SqlTestDatabase database, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await ScalarAsync<int>(
                   database,
                   "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND wait_type LIKE N'LCK[_]M[_]%'") < count)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static async Task<List<string>> StoredChunksAsync(SqlTestDatabase database, string session)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(
            """
            SELECT CONCAT(m.PublicId, ':', e.ChunkIndex) FROM dbo.MessageEmbeddings AS e
            JOIN dbo.Messages AS m ON m.Id = e.MessageId JOIN dbo.Sessions AS s ON s.Id = e.SessionId
            WHERE s.Name = @session
            """,
            connection);
        command.Parameters.AddWithValue("@session", session);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows.Order(StringComparer.Ordinal)];
    }
}
