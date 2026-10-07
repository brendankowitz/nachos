using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

public interface IPeerStore
{
    /// <summary>
    /// Returns the peer, creating it from the given values if missing. An existing peer is returned unchanged.
    /// Safe under concurrent calls for one name: exactly one row results.
    /// </summary>
    Task<PeerRecord> GetOrCreateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    Task<PeerRecord?> GetAsync(string workspaceName, string name, CancellationToken ct);

    /// <summary>Replaces each non-null argument; a null argument leaves that field unchanged.</summary>
    /// <exception cref="NotFoundException">The peer does not exist.</exception>
    Task<PeerRecord> UpdateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    Task<Page<PeerRecord>> ListAsync(
        string workspaceName, FilterNode? filter, PeerKind kind, PageRequest page, CancellationToken ct);

    /// <summary>Sessions the peer is currently an active member of.</summary>
    Task<Page<SessionRecord>> ListSessionsForPeerAsync(
        string workspaceName, string peerName, FilterNode? filter, PageRequest page, CancellationToken ct);
}