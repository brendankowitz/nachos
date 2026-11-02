using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>Peer-card storage: one card per (observer, observed) pair. See <see cref="IMemoryStore"/> for the shared rules.</summary>
public interface IPeerCardStore
{
    /// <summary>Returns the card, or null if none was set.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<PeerCardRecord?> GetAsync(string workspaceName, string observerName, string observedName, CancellationToken ct);

    /// <summary><b>Replaces</b> the card's entries (creating the card if missing), stamping <c>UpdatedAt = now</c>.</summary>
    /// <exception cref="NotFoundException">The workspace or either peer does not exist.</exception>
    Task<PeerCardRecord> SetAsync(
        string workspaceName,
        string observerName,
        string observedName,
        IReadOnlyList<string> entries,
        PeerCardAuthor updatedBy,
        CancellationToken ct);
}
