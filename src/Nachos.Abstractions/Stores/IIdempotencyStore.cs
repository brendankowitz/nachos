using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// Read side and expiry cleanup of idempotency. Records are written by <see cref="IMessageStore.AppendAsync"/>,
/// atomically with the messages they describe.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Returns the record for the key, or null if it is absent or expired. A record is expired when
    /// <c>ExpiresAt &lt;= now</c> on the store's clock, so it expires exactly at its TTL.
    /// </summary>
    Task<IdempotencyRecord?> TryGetAsync(string workspaceName, string key, CancellationToken ct);

    /// <summary>Deletes every expired record (<c>ExpiresAt &lt;= now</c>), in every workspace.</summary>
    /// <returns>How many records were deleted.</returns>
    Task<int> PurgeExpiredAsync(CancellationToken ct);
}
