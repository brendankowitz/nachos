using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <remarks>
/// <para>
/// The backlog is <c>EmbeddingState = 0</c> (pending) on live conclusions, plus <c>SyncState = 0</c> on
/// <c>dbo.MessageEmbeddings</c> chunks whose workspace and session are not being deleted (<c>LifecycleState = 2</c>). It
/// is listed oldest (<c>CreatedAt</c>) first; at equal times conclusions come before chunks, then by row id.
/// </para>
/// <para>
/// A save or a failure applies only while the row is still pending with the claimed content (conclusions: hash, then
/// ordinal comparison; chunks: ordinal comparison with the lengths, as <c>=</c> ignores trailing spaces), so a vector
/// computed for replaced content is never stored.
/// </para>
/// </remarks>
internal sealed class SqlEmbeddingStore(SqlStoreRuntime runtime) : IEmbeddingStore
{
    /// <summary>
    /// A <c>LIKE</c> pattern (under a binary collation) that matches text with a character <see cref="char.IsWhiteSpace(char)"/>
    /// rejects: built from .NET's own whitespace set, so "blank" means exactly <see cref="string.IsNullOrWhiteSpace"/>.
    /// None of the whitespace characters is special inside <c>[^…]</c>.
    /// </summary>
    private static readonly string NotBlankPattern = BuildNotBlankPattern();

    /// <summary>Five parameters per chunk stay under SQL Server's 2,100-parameter limit.</summary>
    private const int InsertChunk = 400;

    /// <summary>The chunks, alias <c>e</c>, with their workspace <c>w</c> and session <c>s</c>.</summary>
    private const string PendingChunksFrom = """
        FROM dbo.MessageEmbeddings AS e
        JOIN dbo.Workspaces AS w ON w.Id = e.WorkspaceId
        JOIN dbo.Sessions AS s ON s.Id = e.SessionId
        """;

    private const string PendingChunksWhere = "WHERE e.SyncState = 0 AND w.LifecycleState <> 2 AND s.LifecycleState <> 2";

    public async Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(int limit, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var db = await runtime.OpenAsync(ct);
        var rows = await db.Database
            .SqlQueryRaw<PendingRow>(
                $"""
                SELECT TOP (@limit) p.Kind, p.WorkspaceName, p.SourceId, p.ChunkIndex, p.Content
                FROM (
                    SELECT * FROM (
                        SELECT TOP (@limit) {(int)PendingEmbeddingKind.Conclusion} AS Kind, w.Name AS WorkspaceName,
                               c.PublicId AS SourceId, 0 AS ChunkIndex, c.Content, c.CreatedAt, c.Id
                        FROM dbo.Conclusions AS c
                        JOIN dbo.Workspaces AS w ON w.Id = c.WorkspaceId
                        WHERE c.EmbeddingState = 0 AND c.DeletedAt IS NULL
                        ORDER BY c.CreatedAt, c.Id) AS conclusions
                    UNION ALL
                    SELECT * FROM (
                        SELECT TOP (@limit) {(int)PendingEmbeddingKind.MessageChunk} AS Kind, w.Name AS WorkspaceName,
                               m.PublicId AS SourceId, CAST(e.ChunkIndex AS int) AS ChunkIndex, e.Content, e.CreatedAt, e.Id
                        {PendingChunksFrom}
                        JOIN dbo.Messages AS m ON m.Id = e.MessageId
                        {PendingChunksWhere}
                        ORDER BY e.CreatedAt, e.Id) AS chunks
                ) AS p
                ORDER BY p.CreatedAt, p.Kind, p.Id
                """,
                SqlParameters.Int("@limit", limit))
            .ToListAsync(ct);
        return [.. rows.Select(row => new PendingEmbedding((PendingEmbeddingKind)row.Kind, row.WorkspaceName, row.SourceId, row.ChunkIndex, row.Content))];
    }

    public async Task<long> CountPendingAsync(CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);
        var counts = await db.Database
            .SqlQueryRaw<long>(
                $"""
                SELECT (SELECT COUNT_BIG(*) FROM dbo.Conclusions WHERE EmbeddingState = 0 AND DeletedAt IS NULL)
                     + (SELECT COUNT_BIG(*) {PendingChunksFrom} {PendingChunksWhere}) AS [Value]
                """)
            .ToListAsync(ct);
        return counts.Single();
    }

    public Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, string embeddingModel, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(embeddingModel);
        Vectors.Require(embedding, nameof(embedding));
        return pending.Kind == PendingEmbeddingKind.MessageChunk
            ? UpdatePendingChunkAsync(pending, "Embedding = @embedding, SyncState = 1", [SqlParameters.Vector("@embedding", embedding)], ct)
            : UpdatePendingConclusionAsync(
                pending,
                $"Embedding = @embedding, EmbeddingState = 1, InternalMetadata = JSON_MODIFY(c.InternalMetadata, '$.{VectorSpace.ModelMetadataKey}', @model)",
                [SqlParameters.Vector("@embedding", embedding), SqlParameters.Text("@model", embeddingModel)],
                ct);
    }

    public Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(pending);
        return pending.Kind == PendingEmbeddingKind.MessageChunk
            ? UpdatePendingChunkAsync(pending, "SyncState = 2", [], ct)
            : UpdatePendingConclusionAsync(pending, "EmbeddingState = 2", [], ct);
    }

    /// <remarks>
    /// Walks <c>dbo.Messages</c> in clustered-key order (insertion order), seeking each message in the unique
    /// <c>(MessageId, ChunkIndex)</c> key of <c>dbo.MessageEmbeddings</c>, and stops at <paramref name="limit"/>; when no
    /// message is unchunked, it still reads them all.
    /// </remarks>
    public async Task<IReadOnlyList<MessageRecord>> ListUnchunkedMessagesAsync(int limit, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var db = await runtime.OpenAsync(ct);
        var rows = await db.Database
            .SqlQueryRaw<UnchunkedRow>(
                """
                SELECT TOP (@limit) w.Name AS WorkspaceName, s.Name AS SessionName, p.Name AS PeerName, m.PublicId, m.Seq,
                       m.Content, m.TokenCount, m.Metadata, m.CreatedAt
                FROM dbo.Messages AS m
                JOIN dbo.Workspaces AS w ON w.Id = m.WorkspaceId
                JOIN dbo.Sessions AS s ON s.Id = m.SessionId
                JOIN dbo.Peers AS p ON p.Id = m.PeerId
                WHERE w.LifecycleState <> 2 AND s.LifecycleState <> 2
                  AND NOT EXISTS (SELECT 1 FROM dbo.MessageEmbeddings AS e WHERE e.MessageId = m.Id)
                  AND m.Content COLLATE Latin1_General_100_BIN2 LIKE @notBlank
                ORDER BY m.Id
                """,
                SqlParameters.Int("@limit", limit),
                SqlParameters.Text("@notBlank", NotBlankPattern))
            .ToListAsync(ct);
        return
        [
            .. rows.Select(row => new MessageRecord(
                row.PublicId,
                row.WorkspaceName,
                row.SessionName,
                row.PeerName,
                row.Seq,
                row.Content,
                row.TokenCount,
                SqlJson.FromStorage(row.Metadata),
                row.CreatedAt)),
        ];
    }

    /// <remarks>
    /// Each batch is one statement: the chunks are matched to their messages by name and public id and inserted unless the
    /// <c>(MessageId, ChunkIndex)</c> key exists, read under <c>UPDLOCK, HOLDLOCK</c> so a concurrent call with the same
    /// chunk waits instead of failing on the unique key. A chunk repeated in the call is inserted once, with its first content.
    /// </remarks>
    public async Task AddChunksAsync(IReadOnlyList<MessageChunk> chunks, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            ArgumentNullException.ThrowIfNull(chunk.WorkspaceName);
            ArgumentNullException.ThrowIfNull(chunk.SessionName);
            ArgumentNullException.ThrowIfNull(chunk.MessageId);
            ArgumentNullException.ThrowIfNull(chunk.Content);
            ArgumentOutOfRangeException.ThrowIfNegative(chunk.ChunkIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(chunk.ChunkIndex, MessageChunk.MaxChunkIndex);
        }

        var unique = chunks.DistinctBy(chunk => (chunk.WorkspaceName, chunk.SessionName, chunk.MessageId, chunk.ChunkIndex)).ToList();
        if (unique.Count == 0)
        {
            return;
        }

        await using var db = await runtime.OpenAsync(ct);
        var now = runtime.Clock.GetUtcNow();
        for (var start = 0; start < unique.Count; start += InsertChunk)
        {
            var count = Math.Min(InsertChunk, unique.Count - start);
            var rows = new List<string>(count);
            var parameters = new List<SqlParameter> { SqlParameters.Time("@now", now) };
            for (var i = start; i < start + count; i++)
            {
                var chunk = unique[i];
                rows.Add($"(@w{i}, @s{i}, @m{i}, @i{i}, @c{i}, {i})");
                parameters.Add(SqlParameters.Text($"@w{i}", chunk.WorkspaceName));
                parameters.Add(SqlParameters.Text($"@s{i}", chunk.SessionName));
                parameters.Add(SqlParameters.Text($"@m{i}", chunk.MessageId));
                parameters.Add(SqlParameters.Int($"@i{i}", chunk.ChunkIndex));
                parameters.Add(SqlParameters.LongText($"@c{i}", chunk.Content));
            }

            await db.Database.ExecuteSqlRawAsync(
                string.Concat(
                    """
                    INSERT dbo.MessageEmbeddings (WorkspaceId, MessageId, SessionId, ChunkIndex, Content, SyncState, CreatedAt)
                    SELECT m.WorkspaceId, m.Id, m.SessionId, j.ChunkIndex, j.Content, 0, @now
                    FROM (VALUES
                    """,
                    string.Join(", ", rows),
                    """
                    ) AS j (Workspace, Session, MessageId, ChunkIndex, Content, Ordinal)
                    JOIN dbo.Workspaces AS w ON w.Name = j.Workspace AND DATALENGTH(w.Name) = DATALENGTH(j.Workspace)
                    JOIN dbo.Sessions AS s ON s.WorkspaceId = w.Id AND s.Name = j.Session AND DATALENGTH(s.Name) = DATALENGTH(j.Session)
                    JOIN dbo.Messages AS m ON m.SessionId = s.Id AND m.PublicId = j.MessageId AND DATALENGTH(m.PublicId) = DATALENGTH(j.MessageId)
                    WHERE w.LifecycleState <> 2 AND s.LifecycleState <> 2
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.MessageEmbeddings AS e WITH (UPDLOCK, HOLDLOCK)
                          WHERE e.MessageId = m.Id AND e.ChunkIndex = j.ChunkIndex)
                    ORDER BY j.Ordinal
                    """),
                parameters,
                ct);
        }
    }

    /// <summary>Applies <c>assignments</c> (fixed SQL chosen by the caller, never user input) while the conclusion is still pending.</summary>
    private async Task UpdatePendingConclusionAsync(PendingEmbedding pending, string assignments, SqlParameter[] values, CancellationToken ct)
    {
        await using var db = await runtime.OpenAsync(ct);
        var sql = string.Concat(
            "UPDATE c SET ",
            assignments,
            """

            FROM dbo.Conclusions AS c
            JOIN dbo.Workspaces AS w ON w.Id = c.WorkspaceId
            WHERE w.Name = @workspace AND DATALENGTH(w.Name) = DATALENGTH(@workspace)
              AND c.PublicId = @publicId AND DATALENGTH(c.PublicId) = DATALENGTH(@publicId)
              AND c.EmbeddingState = 0 AND c.DeletedAt IS NULL
              AND c.ContentHash = @hash AND c.Content = @content COLLATE Latin1_General_100_BIN2_UTF8
            """);
        await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                .. values,
                SqlParameters.Text("@workspace", pending.WorkspaceName),
                SqlParameters.Text("@publicId", pending.SourceId),
                SqlParameters.Binary("@hash", ContentHash.Of(pending.Content)),
                SqlParameters.LongText("@content", pending.Content),
            ],
            ct);
    }

    /// <summary>Applies <c>assignments</c> (fixed SQL chosen by the caller, never user input) while the chunk is still pending.</summary>
    private async Task UpdatePendingChunkAsync(PendingEmbedding pending, string assignments, SqlParameter[] values, CancellationToken ct)
    {
        await using var db = await runtime.OpenAsync(ct);
        var sql = string.Concat(
            "UPDATE e SET ",
            assignments,
            """

            FROM dbo.MessageEmbeddings AS e
            JOIN dbo.Messages AS m ON m.Id = e.MessageId
            JOIN dbo.Workspaces AS w ON w.Id = e.WorkspaceId
            WHERE w.Name = @workspace AND DATALENGTH(w.Name) = DATALENGTH(@workspace)
              AND m.PublicId = @publicId AND DATALENGTH(m.PublicId) = DATALENGTH(@publicId)
              AND e.ChunkIndex = @chunkIndex AND e.SyncState = 0
              AND e.Content = @content COLLATE Latin1_General_100_BIN2 AND DATALENGTH(e.Content) = DATALENGTH(@content)
            """);
        await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                .. values,
                SqlParameters.Text("@workspace", pending.WorkspaceName),
                SqlParameters.Text("@publicId", pending.SourceId),
                SqlParameters.Int("@chunkIndex", pending.ChunkIndex),
                SqlParameters.LongText("@content", pending.Content),
            ],
            ct);
    }

    private static string BuildNotBlankPattern()
    {
        var pattern = new StringBuilder("%[^");
        for (var c = char.MinValue; c < char.MaxValue; c++)
        {
            if (char.IsWhiteSpace(c))
            {
                pattern.Append(c);
            }
        }

        return pattern.Append("]%").ToString();
    }

    private sealed record PendingRow(int Kind, string WorkspaceName, string SourceId, int ChunkIndex, string Content);

    private sealed record UnchunkedRow(
        string WorkspaceName,
        string SessionName,
        string PeerName,
        string PublicId,
        long Seq,
        string Content,
        int TokenCount,
        string Metadata,
        DateTimeOffset CreatedAt);
}
