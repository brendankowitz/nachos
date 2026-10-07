using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

public interface ISessionStore
{
    /// <summary>
    /// Returns the session, creating it if missing; an existing session's fields are returned unchanged.
    /// Either way, every listed peer is ensured to be an active member with its config applied.
    /// </summary>
    Task<SessionRecord> GetOrCreateAsync(
        string workspaceName,
        string name,
        JsonObject? metadata,
        JsonObject? configuration,
        IReadOnlyDictionary<string, SessionPeerConfig>? peers,
        CancellationToken ct);

    Task<SessionRecord?> GetAsync(string workspaceName, string name, CancellationToken ct);

    /// <summary>Replaces each non-null argument; a null argument leaves that field unchanged.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<SessionRecord> UpdateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    Task<Page<SessionRecord>> ListAsync(
        string workspaceName, FilterNode? filter, PageRequest page, CancellationToken ct);

    /// <summary>Adds the peers (creating them if needed) as active members and applies their config.</summary>
    Task AddPeersAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct);

    /// <summary>Makes exactly the listed peers active members; members not listed get <c>LeftAt = now</c>.</summary>
    Task SetPeersAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct);

    /// <summary>Sets <c>LeftAt = now</c> on each named member.</summary>
    Task RemovePeersAsync(
        string workspaceName, string sessionName, IReadOnlyList<string> peerNames, CancellationToken ct);

    /// <summary>Active members only.</summary>
    Task<Page<PeerRecord>> ListPeersAsync(
        string workspaceName, string sessionName, PageRequest page, CancellationToken ct);

    Task<SessionPeerConfig> GetPeerConfigAsync(
        string workspaceName, string sessionName, string peerName, CancellationToken ct);

    Task SetPeerConfigAsync(
        string workspaceName, string sessionName, string peerName, SessionPeerConfig config, CancellationToken ct);

    Task<bool> IsActiveMemberAsync(
        string workspaceName, string sessionName, string peerName, CancellationToken ct);
}