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
        return [.. rows.Select(row => new PendingEmbedding(row.WorkspaceName, row.PublicId, row.Content))];
    }

    public Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(pending);
        Vectors.Require(embedding, nameof(embedding));
        return UpdatePendingAsync(pending, "Embedding = @embedding, EmbeddingState = 1", [SqlParameters.Vector("@embedding", embedding)], ct);
    }

    public Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(pending);
        return UpdatePendingAsync(pending, "EmbeddingState = 2", [], ct);
    }

    /// <summary>Applies <c>assignments</c> (fixed SQL chosen by the caller, never user input) while the row is still pending.</summary>
    private async Task UpdatePendingAsync(PendingEmbedding pending, string assignments, SqlParameter[] values, CancellationToken ct)
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
                SqlParameters.Text("@publicId", pending.ConclusionId),
                SqlParameters.Binary("@hash", ContentHash.Of(pending.Content)),
                SqlParameters.LongText("@content", pending.Content),
            ],
            ct);
    }

    private sealed record PendingRow(string WorkspaceName, string PublicId, string Content);
}
