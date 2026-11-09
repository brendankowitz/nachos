using Microsoft.Data.SqlClient;
using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;
using static Nachos.DataLayer.SqlServer.Tests.SchemaTestSql;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The M3 recall tables (MessageEmbeddings, SessionSummaries, EmbeddingSpace) deploy additively at schema version 1 on the Full-Text Search image,
/// and a database stamped current without them is refused with the fix command.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class M3SchemaTests(SqlServerFixture fixture)
{
    private async Task<string> DeployedDatabaseAsync()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await DeployAsync(connectionString);
        return connectionString;
    }

    // A workspace "w1" with peer "a", session "s" and messages 1 and 2; a workspace "w2" with a peer and a session of its own.
    private const string SeedSql = """
        INSERT [dbo].[Workspaces] ([Name], [LifecycleState], [CreatedAt]) VALUES (N'w1', 0, SYSDATETIMEOFFSET()), (N'w2', 0, SYSDATETIMEOFFSET());
        INSERT [dbo].[Peers] ([WorkspaceId], [Name], [CreatedAt]) SELECT [Id], N'a', SYSDATETIMEOFFSET() FROM [dbo].[Workspaces];
        INSERT [dbo].[Sessions] ([WorkspaceId], [Name], [LifecycleState], [CreatedAt]) SELECT [Id], N's', 0, SYSDATETIMEOFFSET() FROM [dbo].[Workspaces];
        INSERT [dbo].[Messages] ([WorkspaceId], [SessionId], [PeerId], [PublicId], [Seq], [Content], [TokenCount], [CreatedAt])
            SELECT s.[WorkspaceId], s.[Id], p.[Id], CONCAT(N'm', s.[WorkspaceId], N'_', n.[Seq]), n.[Seq], N'hello', 1, SYSDATETIMEOFFSET()
            FROM [dbo].[Sessions] s
            JOIN [dbo].[Peers] p ON p.[WorkspaceId] = s.[WorkspaceId]
            CROSS JOIN (VALUES (1), (2)) n ([Seq]);
        """;

    private static string InsertEmbedding(string workspace, int messageSeq, int chunk, int syncState, string? sessionWorkspace = null) =>
        $"""
        INSERT [dbo].[MessageEmbeddings] ([WorkspaceId], [MessageId], [SessionId], [ChunkIndex], [Content], [SyncState], [CreatedAt])
        SELECT m.[WorkspaceId], m.[Id], s.[Id], {chunk}, N'c', {syncState}, SYSDATETIMEOFFSET()
        FROM [dbo].[Workspaces] w
        JOIN [dbo].[Messages] m ON m.[WorkspaceId] = w.[Id] AND m.[Seq] = {messageSeq}
        JOIN [dbo].[Workspaces] sw ON sw.[Name] = N'{sessionWorkspace ?? workspace}'
        JOIN [dbo].[Sessions] s ON s.[WorkspaceId] = sw.[Id] AND s.[Name] = N's'
        WHERE w.[Name] = N'{workspace}'
        """;

    private static string InsertSummary(string workspace, int kind, int coversSeq) =>
        $"""
        INSERT [dbo].[SessionSummaries] ([WorkspaceId], [SessionId], [Kind], [Content], [CoversThroughMessageId], [CoversThroughSeq], [TokenCount], [CreatedAt])
        SELECT m.[WorkspaceId], m.[SessionId], {kind}, N's', m.[Id], m.[Seq], 1, SYSDATETIMEOFFSET()
        FROM [dbo].[Messages] m JOIN [dbo].[Workspaces] w ON w.[Id] = m.[WorkspaceId]
        WHERE w.[Name] = N'{workspace}' AND m.[Seq] = {coversSeq}
        """;

    private static async Task RejectsAsync(string connectionString, string sql, string constraint)
    {
        var rejected = await Should.ThrowAsync<SqlException>(() => ExecuteAsync(connectionString, sql));
        rejected.Message.ShouldContain(constraint);
    }

    [Fact]
    public async Task FtsFixture_ReportsFullTextInstalled()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        (await ScalarAsync<int>(connectionString, "SELECT CAST(SERVERPROPERTY('IsFullTextInstalled') AS int)")).ShouldBe(1);
    }

    [Fact]
    public async Task M3Tables_DeployToEmptyDatabase()
    {
        var connectionString = await DeployedDatabaseAsync();

        (await M3TableCountAsync(connectionString)).ShouldBe(M3Tables.Length);
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
        (await Deployer(connectionString).ReportAsync(default)).HasPendingChanges.ShouldBeFalse();
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.fulltext_indexes")).ShouldBe(0);
        SchemaInfo.CurrentVersion.ShouldBe(1);
    }

    [Fact]
    public async Task M2Database_ReviewedUpgrade_AddsM3Tables()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "INSERT [dbo].[Workspaces] ([Name], [LifecycleState], [CreatedAt]) VALUES (N'kept', 0, SYSDATETIMEOFFSET())");
        // What a database deployed by an earlier M2 build looks like: stamped version 1, none of the M3 tables, and no
        // alternate key on Messages (the M3 composite FKs need it).
        await ExecuteAsync(
            connectionString,
            """
            DROP TABLE [dbo].[SessionSummaries];
            DROP TABLE [dbo].[MessageEmbeddings];
            DROP TABLE [dbo].[EmbeddingSpace];
            DROP INDEX [UX_Messages_Workspace_Session_Id] ON [dbo].[Messages];
            """);
        var deployer = Deployer(connectionString);

        var report = await deployer.ReportAsync(default);
        report.HasPendingChanges.ShouldBeTrue();
        report.Classification.ShouldBe(DeployClassification.AutoSafe, string.Join(' ', report.Reasons));

        var applied = await deployer.DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, default);

        applied.Applied.ShouldBeTrue();
        (await M3TableCountAsync(connectionString)).ShouldBe(M3Tables.Length);
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM [dbo].[Workspaces] WHERE [Name] = N'kept'")).ShouldBe(1);
        (await deployer.ReportAsync(default)).HasPendingChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task StampedCurrent_MissingM3Table_FailsReadinessWithFixCommand()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "DROP TABLE [dbo].[SessionSummaries]");
        const string Expected =
            "Schema is stamped current but table SessionSummaries is missing; recreate the database or run 'nachos schema upgrade --approve-reviewed'.";

        var status = await Should.ThrowAsync<InvalidOperationException>(() => Deployer(connectionString).GetStatusAsync(default));
        var gate = Gate(connectionString);
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => gate.EnsureAsync(default));

        status.Message.ShouldBe(Expected);
        refused.Message.ShouldBe(Expected);

        await Deployer(connectionString).DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, default);

        await Gate(connectionString).EnsureAsync(default);
    }

    [Fact]
    public async Task AllIndexKeys_WithinSqlLimits()
    {
        var connectionString = await DeployedDatabaseAsync();
        const string sql = """
            SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, i.type AS IndexType,
                   SUM(CAST(c.max_length AS int)) AS KeyBytes
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id AND t.is_ms_shipped = 0
            WHERE i.type IN (1, 2)
            GROUP BY i.object_id, i.index_id, i.name, i.type
            """;

        var checkedTables = new HashSet<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (!M3Tables.Contains(table) && table != "Messages")
            {
                continue;
            }

            checkedTables.Add(table);
            var limit = reader.GetByte(2) == 1 ? 900 : 1700;
            reader.GetInt32(3).ShouldBeLessThanOrEqualTo(limit, $"{table}.{reader.GetString(1)} key size");
        }

        checkedTables.ShouldBe([.. M3Tables, "Messages"], ignoreOrder: true);
    }

    [Fact]
    public async Task EmbeddingVector_Dimension1536_OnTheDeployedDatabase()
    {
        var connectionString = await DeployedDatabaseAsync();

        var dimensions = await ScalarAsync<int>(
            connectionString,
            "SELECT vector_dimensions FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.MessageEmbeddings') AND name = N'Embedding'");

        dimensions.ShouldBe(1536);
    }

    [Fact]
    public async Task MessageEmbeddings_EnforceKeysChecksAndWorkspaceScope()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, SeedSql);

        await ExecuteAsync(connectionString, InsertEmbedding("w1", 1, 0, 2));
        await ExecuteAsync(connectionString, InsertEmbedding("w1", 1, 1, 0));
        await RejectsAsync(connectionString, InsertEmbedding("w1", 1, 0, 0), "UQ_MessageEmbeddings_Message_Chunk");
        await RejectsAsync(connectionString, InsertEmbedding("w1", 2, 0, 3), "CK_MessageEmbeddings_SyncState");
        await RejectsAsync(connectionString, InsertEmbedding("w1", 2, 0, 0, sessionWorkspace: "w2"), "FK_MessageEmbeddings_Messages");
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM [dbo].[MessageEmbeddings] WHERE [Attempts] = 0 AND [Embedding] IS NULL")).ShouldBe(2);
    }

    [Fact]
    public async Task MessageEmbeddings_RejectASessionOtherThanTheMessagesOwn()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, SeedSql);
        await ExecuteAsync(
            connectionString,
            "INSERT [dbo].[Sessions] ([WorkspaceId], [Name], [LifecycleState], [CreatedAt]) SELECT [Id], N'other', 0, SYSDATETIMEOFFSET() FROM [dbo].[Workspaces] WHERE [Name] = N'w1'");

        await RejectsAsync(
            connectionString,
            """
            INSERT [dbo].[MessageEmbeddings] ([WorkspaceId], [MessageId], [SessionId], [ChunkIndex], [Content], [SyncState], [CreatedAt])
            SELECT m.[WorkspaceId], m.[Id], o.[Id], 0, N'c', 0, SYSDATETIMEOFFSET()
            FROM [dbo].[Messages] m JOIN [dbo].[Sessions] o ON o.[WorkspaceId] = m.[WorkspaceId] AND o.[Name] = N'other'
            WHERE m.[Seq] = 1
            """,
            "FK_MessageEmbeddings_Messages");
    }

    [Fact]
    public async Task SessionSummaries_RejectACoveredMessageFromAnotherSession()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, SeedSql);
        await ExecuteAsync(
            connectionString,
            "INSERT [dbo].[Sessions] ([WorkspaceId], [Name], [LifecycleState], [CreatedAt]) SELECT [Id], N'other', 0, SYSDATETIMEOFFSET() FROM [dbo].[Workspaces] WHERE [Name] = N'w1'");

        await RejectsAsync(
            connectionString,
            """
            INSERT [dbo].[SessionSummaries] ([WorkspaceId], [SessionId], [Kind], [Content], [CoversThroughMessageId], [CoversThroughSeq], [TokenCount], [CreatedAt])
            SELECT m.[WorkspaceId], o.[Id], 1, N's', m.[Id], m.[Seq], 1, SYSDATETIMEOFFSET()
            FROM [dbo].[Messages] m JOIN [dbo].[Sessions] o ON o.[WorkspaceId] = m.[WorkspaceId] AND o.[Name] = N'other'
            WHERE m.[Seq] = 1
            """,
            "FK_SessionSummaries_Messages");
    }

    [Fact]
    public async Task EmbeddingSpace_HoldsOneRow()
    {
        var connectionString = await DeployedDatabaseAsync();
        const string Insert = "INSERT [dbo].[EmbeddingSpace] ([Id], [Model], [Dimensions], [StampedAt]) VALUES ({0}, N'bge-small-en-v1.5', 384, SYSDATETIMEOFFSET())";

        await ExecuteAsync(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, 1));
        await RejectsAsync(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, 1), "PK_EmbeddingSpace");
        await RejectsAsync(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, 2), "CK_EmbeddingSpace_SingleRow");
    }

    [Fact]
    public async Task SessionSummaries_EnforceOneCurrentSummaryPerKind()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, SeedSql);

        await ExecuteAsync(connectionString, InsertSummary("w1", 1, 2));
        await ExecuteAsync(connectionString, InsertSummary("w1", 2, 2));
        await RejectsAsync(connectionString, InsertSummary("w1", 1, 1), "UQ_SessionSummaries_Session_Kind");
        await RejectsAsync(connectionString, InsertSummary("w2", 3, 1), "CK_SessionSummaries_Kind");
        await RejectsAsync(
            connectionString,
            """
            INSERT [dbo].[SessionSummaries] ([WorkspaceId], [SessionId], [Kind], [Content], [CoversThroughMessageId], [CoversThroughSeq], [TokenCount], [CreatedAt])
            SELECT s.[WorkspaceId], s.[Id], 1, N's', m.[Id], m.[Seq], 1, SYSDATETIMEOFFSET()
            FROM [dbo].[Sessions] s JOIN [dbo].[Workspaces] w ON w.[Id] = s.[WorkspaceId] AND w.[Name] = N'w2'
            CROSS JOIN [dbo].[Messages] m JOIN [dbo].[Workspaces] mw ON mw.[Id] = m.[WorkspaceId] AND mw.[Name] = N'w1'
            WHERE m.[Seq] = 1
            """,
            "FK_SessionSummaries_Messages");
    }
}
