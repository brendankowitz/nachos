using Microsoft.Data.SqlClient;
using Shouldly;

namespace Nachos.Cli.Tests;

[Collection(SqlServerDockerGroup.Name)]
public sealed class EmbeddingCommandTests(SqlServerFixture fixture)
{
    private string Password => new SqlConnectionStringBuilder(fixture.ServerConnectionString).Password;

    private async Task<CliRun> ResetAsync(string connectionString, params string[] args)
    {
        var run = await CliRun.RunAsync(["embeddings", "reset", .. args, "--connection", connectionString]);
        ConnectionStringDisclosureTests.ShouldNotLeak(run, Password);
        return run;
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Reset_ClearsVectorsToPending_AndRestamps()
    {
        var cs = await fixture.CreateDatabaseAsync();
        (await CliRun.RunAsync("schema", "upgrade", "--connection", cs)).ExitCode.ShouldBe(0);
        await ScalarAsync(
            cs,
            """
            INSERT dbo.EmbeddingSpace (Id, Model, Dimensions, StampedAt) VALUES (1, N'text-embedding-3-small', 1536, SYSDATETIMEOFFSET());
            INSERT dbo.Workspaces (Name, LifecycleState, CreatedAt) VALUES (N'w', 0, SYSDATETIMEOFFSET());
            INSERT dbo.Peers (WorkspaceId, Name, CreatedAt) SELECT Id, N'p', SYSDATETIMEOFFSET() FROM dbo.Workspaces;
            INSERT dbo.Sessions (WorkspaceId, Name, LifecycleState, CreatedAt) SELECT Id, N's', 0, SYSDATETIMEOFFSET() FROM dbo.Workspaces;
            INSERT dbo.Messages (WorkspaceId, SessionId, PeerId, PublicId, Seq, Content, TokenCount, CreatedAt)
                SELECT s.WorkspaceId, s.Id, p.Id, N'm1', 1, N'hello', 1, SYSDATETIMEOFFSET() FROM dbo.Sessions s JOIN dbo.Peers p ON p.WorkspaceId = s.WorkspaceId;
            INSERT dbo.MessageEmbeddings (WorkspaceId, MessageId, SessionId, ChunkIndex, Content, SyncState, CreatedAt)
                SELECT WorkspaceId, Id, SessionId, 0, N'hello', 2, SYSDATETIMEOFFSET() FROM dbo.Messages;
            """);

        var run = await ResetAsync(cs, "--model", "bge-small-en-v1.5");

        run.ExitCode.ShouldBe(0, run.Error);
        run.Out.Trim().ShouldBe("""{"model":"bge-small-en-v1.5","reset":1}""");
        (await ScalarAsync(cs, "SELECT CONCAT(Model, N'/', Dimensions) FROM dbo.EmbeddingSpace")).ShouldBe("bge-small-en-v1.5/384");
        (await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.MessageEmbeddings WHERE SyncState = 0")).ShouldBe(1);
    }

    [Fact]
    public async Task Reset_SchemaNotCurrent_IsRefused()
    {
        var cs = await fixture.CreateDatabaseAsync();

        var run = await ResetAsync(cs, "--model", "bge-small-en-v1.5");

        run.ExitCode.ShouldBe(2);
        run.Error.ShouldContain("nachos schema upgrade");
    }
}
