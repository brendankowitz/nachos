using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Testing;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The M3 SQL paths that leave out a workspace or session being deleted (<c>LifecycleState = 2</c>). The state is set
/// directly in the database on purpose: it isolates the read paths from deletion acceptance (covered by
/// <see cref="SqlDeletionContractTests"/> and <see cref="SqlDeletionBarrierTests"/>), and it can mark a workspace whose
/// sessions are still active, which acceptance would refuse.
/// </summary>
[Collection(M2StoreContracts.Name)]
public sealed class SqlM3DeletingStateTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private SqlTestDatabase? _database;

    private static CancellationToken Ct => CancellationToken.None;

    private SqlTestDatabase Database => _database ?? throw new InvalidOperationException("The database is not initialized.");

    public async Task InitializeAsync()
    {
        _database = await SqlTestDatabase.GetAsync(fixture, "m3-deleting-state");
        await Database.ResetQueueAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("Workspaces")]
    [InlineData("Sessions")]
    public async Task DeletingResource_IsLeftOutOfSearchBacklogAndSummaries(string table)
    {
        var store = Database.CreateStore(new FakeTimeProvider(Start));
        var workspace = $"ws-{Guid.NewGuid():N}";
        var session = $"sess-{Guid.NewGuid():N}";
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        var messages = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend(
                [.. Enumerable.Range(0, 20).Select(i => new NewMessage("bob", $"quokka {i}", 2, null, null))],
                Chunks: [.. Enumerable.Range(0, 20).Select(i => (IReadOnlyList<string>)[$"quokka {i}"])],
                Summary: new SummaryIntent(20, 60)),
            Ct);
        var legacy = (await store.Messages.AppendAsync(workspace, session, [new NewMessage("bob", "quokka legacy", 2, null, null)], null, Ct)).Single();
        var firstChunk = (await store.Embeddings.ListPendingAsync(100_000, Ct)).First(p => p.SourceId == messages[0].PublicId);
        await store.Embeddings.SaveAsync(firstChunk, Vector(), "test-model", Ct);
        var lease = (await store.WorkQueue.TryAcquireAsync(
            "worker-1",
            new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Summary] = WorkBatchPolicy.Immediate },
            TimeSpan.FromMinutes(5),
            Ct)).ShouldNotBeNull();

        var requests = new[]
        {
            new MessageSearchRequest(workspace, new MessageSearchScope.Workspace(), null),
            new MessageSearchRequest(workspace, new MessageSearchScope.Session(session), null),
            new MessageSearchRequest(workspace, new MessageSearchScope.PeerPerspective("bob"), null),
        };
        foreach (var request in requests)
        {
            (await store.Search.LexicalAsync(request, ["quokka"], 100, Ct)).ShouldNotBeEmpty();
            (await store.Search.VectorAsync(request, Vector(), 100, null, Ct)).ShouldNotBeEmpty();
        }

        (await store.Embeddings.ListUnchunkedMessagesAsync(100_000, Ct)).ShouldContain(m => m.WorkspaceName == workspace);
        var pendingBefore = await store.Embeddings.CountPendingAsync(Ct);

        await ExecuteAsync(
            $"UPDATE dbo.{table} SET LifecycleState = 2 WHERE Name = @name",
            new SqlParameter("@name", table == "Workspaces" ? workspace : session));

        foreach (var request in requests)
        {
            (await store.Search.LexicalAsync(request, ["quokka"], 100, Ct)).ShouldBeEmpty();
            (await store.Search.VectorAsync(request, Vector(), 100, null, Ct)).ShouldBeEmpty();
        }

        // The 19 chunks still pending leave the backlog.
        (await store.Embeddings.ListPendingAsync(100_000, Ct)).ShouldNotContain(p => p.WorkspaceName == workspace);
        (await store.Embeddings.CountPendingAsync(Ct)).ShouldBe(pendingBefore - 19);
        (await store.Embeddings.ListUnchunkedMessagesAsync(100_000, Ct)).ShouldNotContain(m => m.WorkspaceName == workspace);
        await store.Embeddings.AddChunksAsync([new MessageChunk(workspace, session, legacy.PublicId, 0, "quokka legacy")], Ct);
        (await ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.MessageEmbeddings AS e JOIN dbo.Messages AS m ON m.Id = e.MessageId WHERE m.PublicId = @id",
                new SqlParameter("@id", legacy.PublicId)))
            .ShouldBe(0);

        // The summary is discarded and its item completes as resource_deleted.
        await store.Summaries.SaveAsync(
            lease, workspace, session, [new NewSummary(SummaryKind.Short, "discarded", messages[19].PublicId, 20, 1)], Ct);
        (await ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.SessionSummaries AS ss JOIN dbo.Sessions AS s ON s.Id = ss.SessionId WHERE s.Name = @session",
                new SqlParameter("@session", session)))
            .ShouldBe(0);
        var item = lease.Items.ShouldHaveSingleItem();
        (await ScalarAsync<string>(
                "SELECT CONCAT(Status, ':', Error) FROM dbo.WorkItems WHERE Id = @id", new SqlParameter("@id", item.Id)))
            .ShouldBe("2:resource_deleted");
        await Should.ThrowAsync<LeaseLostException>(() => store.WorkQueue.RenewAsync(lease, TimeSpan.FromMinutes(5), Ct));
    }

    private static float[] Vector()
    {
        var vector = new float[VectorSpace.Dimensions];
        vector[0] = 1;
        return vector;
    }

    private async Task ExecuteAsync(string sql, SqlParameter parameter)
    {
        await using var connection = new SqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, SqlParameter parameter)
    {
        await using var connection = new SqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(parameter);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
