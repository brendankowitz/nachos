using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// Session and membership storage. See <see cref="IMemoryStore"/> for the rules shared by all stores.
/// </summary>
/// <remarks>
/// Every operation that takes a session name throws <see cref="NotFoundException"/> when the session (or its
/// workspace) does not exist.
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// Returns the session, creating it if missing (null values become <c>{}</c>); an existing session's fields are
    /// returned unchanged. In both cases every listed peer is ensured to be an active member (created if needed)
    /// with its config set from <paramref name="peers"/>. Concurrent calls yield exactly one row.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<SessionRecord> GetOrCreateAsync(
        string workspaceName,
        string name,
        JsonObject? metadata,
        JsonObject? configuration,
        IReadOnlyDictionary<string, SessionPeerConfig>? peers,
        CancellationToken ct);

    /// <summary>Returns the session, or null if it does not exist.</summary>
    Task<SessionRecord?> GetAsync(string workspaceName, string name, CancellationToken ct);

    /// <summary>
    /// <b>Replaces</b> metadata and/or configuration wholesale when the argument is non-null; a null argument leaves
    /// that field unchanged.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<SessionRecord> UpdateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    /// <summary>Lists the workspace's sessions in creation order (reversed when <see cref="PageRequest.Reverse"/>).</summary>
    Task<Page<SessionRecord>> ListAsync(
        string workspaceName, FilterNode? filter, PageRequest page, CancellationToken ct);

    /// <summary>
    /// Makes the peers active members (creating the peers if needed) and sets each one's config from the argument.
    /// A peer that previously left is reactivated in place: <c>LeftAt = null</c>, <c>JoinedAt = now</c>, config from
    /// the argument; a duplicate membership row is never created.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task AddPeersAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct);

    /// <summary>
    /// Makes exactly the listed peers the active members: listed peers are added or reactivated as in
    /// <see cref="AddPeersAsync"/>, and active members not listed get <c>LeftAt = now</c>.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task SetPeersAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct);

    /// <summary>
    /// Sets <c>LeftAt = now</c> on each named active member. Naming a peer that is not an active member is an
    /// idempotent no-op.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task RemovePeersAsync(
        string workspaceName, string sessionName, IReadOnlyList<string> peerNames, CancellationToken ct);

    /// <summary>Lists the <b>active</b> members only, in peer creation order (reversed when <see cref="PageRequest.Reverse"/>).</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Page<PeerRecord>> ListPeersAsync(
        string workspaceName, string sessionName, PageRequest page, CancellationToken ct);

    /// <summary>Returns the member's config.</summary>
    /// <exception cref="NotFoundException">The session does not exist, or the peer is not an active member.</exception>
    Task<SessionPeerConfig> GetPeerConfigAsync(
        string workspaceName, string sessionName, string peerName, CancellationToken ct);

    /// <summary>Replaces the member's config.</summary>
    /// <exception cref="NotFoundException">The session does not exist, or the peer is not an active member.</exception>
    Task SetPeerConfigAsync(
        string workspaceName, string sessionName, string peerName, SessionPeerConfig config, CancellationToken ct);

    /// <summary>True when the peer is currently an active member; false for non-members and former members.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<bool> IsActiveMemberAsync(
        string workspaceName, string sessionName, string peerName, CancellationToken ct);
}