using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// Read side of idempotency. Records are written by <see cref="IMessageStore.AppendAsync"/>, atomically with
/// the messages they describe.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Returns the record for the key, or null if absent or expired.</summary>
    Task<IdempotencyRecord?> TryGetAsync(string workspaceName, string key, CancellationToken ct);
}