using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

/// <summary>Peer storage. See <see cref="IMemoryStore"/> for the rules shared by all stores.</summary>
public interface IPeerStore
{
    /// <summary>
    /// Returns the peer, creating it from the given values if missing (null values become <c>{}</c>). An existing
    /// peer is returned unchanged. Concurrent calls for one name yield exactly one row and identical results.
    /// </summary>
    /// <param name="isInternal">Marks a new peer as internal (a scope peer). Ignored when the peer already exists.</param>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<PeerRecord> GetOrCreateAsync(
        string workspaceName,
        string name,
        JsonObject? metadata,
        JsonObject? configuration,
        CancellationToken ct,
        bool isInternal = false);

    /// <summary>Returns the peer, or null if it does not exist.</summary>
    Task<PeerRecord?> GetAsync(string workspaceName, string name, CancellationToken ct);

    /// <summary>
    /// <b>Replaces</b> metadata and/or configuration wholesale when the argument is non-null; a null argument leaves
    /// that field unchanged.
    /// </summary>
    /// <exception cref="NotFoundException">The peer does not exist.</exception>
    Task<PeerRecord> UpdateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    /// <summary>
    /// Lists the workspace's peers selected by <paramref name="kind"/> in creation order (reversed when
    /// <see cref="PageRequest.Reverse"/>).
    /// </summary>
    Task<Page<PeerRecord>> ListAsync(
        string workspaceName, PeerKind kind, FilterNode? filter, PageRequest page, CancellationToken ct);

    /// <summary>
    /// Lists the sessions the peer is currently an <b>active</b> member of (sessions it has left are excluded), in
    /// session creation order (reversed when <see cref="PageRequest.Reverse"/>).
    /// </summary>
    Task<Page<SessionRecord>> ListSessionsForPeerAsync(
        string workspaceName, string peerName, FilterNode? filter, PageRequest page, CancellationToken ct);
}