using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

internal sealed class SqlIdempotencyStore(SqlStoreRuntime runtime) : IIdempotencyStore
{
    public async Task<IdempotencyRecord?> TryGetAsync(string workspaceName, string key, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        if (await Lookups.FindWorkspaceIdAsync(db, workspaceName, ct) is not { } workspaceId)
        {
            return null;
        }

        // Keys are looked up by hash (the unique index) and confirmed exactly; ExpiresAt <= now is expired.
        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var now = runtime.Clock.GetUtcNow();
        var record = await db.IdempotencyRecords.AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && r.KeyHash == keyHash && r.ExpiresAt > now)
            .FirstOrDefaultAsync(ct);
        return record is null || !string.Equals(record.Key, key, StringComparison.Ordinal)
            ? null
            : new IdempotencyRecord(record.Key, record.RequestHash, record.ResponseStatus, record.ResponseBody, record.ExpiresAt);
    }

    /// <remarks>
    /// Seeks <c>IX_IdempotencyRecords_ExpiresAt</c>, deleting in batches below SQL Server's lock-escalation threshold
    /// (5000 locks) so a large backlog never locks the whole table against concurrent appends.
    /// </remarks>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        const int batchSize = 4000;
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);
        var now = runtime.Clock.GetUtcNow();
        var purged = 0;
        int deleted;
        do
        {
            deleted = await db.IdempotencyRecords.Where(r => r.ExpiresAt <= now).Take(batchSize).ExecuteDeleteAsync(ct);
            purged += deleted;
        }
        while (deleted == batchSize);

        return purged;
    }
}
