using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <remarks>
/// The backlog is <c>EmbeddingState = 0</c> (pending) on live conclusions. A save or a failure applies only while the row is
/// still pending with the claimed content (hash, then ordinal comparison), so a vector computed for replaced content is
/// never stored.
/// </remarks>
internal sealed class SqlEmbeddingStore(SqlStoreRuntime runtime) : IEmbeddingStore
{
    public async Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(int limit, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var db = await runtime.OpenAsync(ct);
        var rows = await db.Database
            .SqlQueryRaw<PendingRow>(
                """
                SELECT TOP (@limit) w.Name AS WorkspaceName, c.PublicId, c.Content
                FROM dbo.Conclusions AS c
                JOIN dbo.Workspaces AS w ON w.Id = c.WorkspaceId
                WHERE c.EmbeddingState = 0 AND c.DeletedAt IS NULL
                ORDER BY c.CreatedAt, c.Id
                """,
                SqlParameters.Int("@limit", limit))
            .ToListAsync(ct);
        return [.. rows.Select(row => PendingEmbedding.ForConclusion(row.WorkspaceName, row.PublicId, row.Content))];
    }

    public async Task<long> CountPendingAsync(CancellationToken ct) =>
        await CountPendingAsync(PendingEmbeddingKind.Conclusion, ct) + await CountPendingAsync(PendingEmbeddingKind.MessageChunk, ct);

    public async Task<long> CountPendingAsync(PendingEmbeddingKind kind, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        var sql = kind switch
        {
            PendingEmbeddingKind.Conclusion =>
                "SELECT COUNT_BIG(*) AS [Value] FROM dbo.Conclusions WHERE EmbeddingState = 0 AND DeletedAt IS NULL",
            PendingEmbeddingKind.MessageChunk =>
                "SELECT COUNT_BIG(*) AS [Value] FROM dbo.MessageEmbeddings WHERE SyncState = 0",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown pending embedding kind."),
        };
        await using var db = await runtime.OpenAsync(ct);
        var counts = await db.Database.SqlQueryRaw<long>(sql).ToListAsync(ct);
        return counts.Single();
    }

    public Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, string embeddingModel, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(embeddingModel);
        Vectors.Require(embedding, nameof(embedding));
        return UpdatePendingAsync(
            pending,
            $"Embedding = @embedding, EmbeddingState = 1, InternalMetadata = JSON_MODIFY(c.InternalMetadata, '$.{VectorSpace.ModelMetadataKey}', @model)",
            [SqlParameters.Vector("@embedding", embedding), SqlParameters.Text("@model", embeddingModel)],
            ct);
    }

    public Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(pending);
        return UpdatePendingAsync(pending, "EmbeddingState = 2", [], ct);
    }

    /// <exception cref="NotSupportedException">Message chunks arrive in M3 Task 3.</exception>
    public Task<IReadOnlyList<MessageRecord>> ListUnchunkedMessagesAsync(int limit, CancellationToken ct) =>
        throw new NotSupportedException(UnimplementedRecallStores.Message);

    /// <exception cref="NotSupportedException">Message chunks arrive in M3 Task 3.</exception>
    public Task AddChunksAsync(IReadOnlyList<MessageChunk> chunks, CancellationToken ct) =>
        throw new NotSupportedException(UnimplementedRecallStores.Message);

    /// <summary>Applies <c>assignments</c> (fixed SQL chosen by the caller, never user input) while the row is still pending.</summary>
    /// <exception cref="NotSupportedException"><paramref name="pending"/> is a message chunk, which arrives in M3 Task 3.</exception>
    private async Task UpdatePendingAsync(PendingEmbedding pending, string assignments, SqlParameter[] values, CancellationToken ct)
    {
        if (pending.Kind != PendingEmbeddingKind.Conclusion)
        {
            throw new NotSupportedException(UnimplementedRecallStores.Message);
        }

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

    private sealed record PendingRow(string WorkspaceName, string PublicId, string Content);
}
