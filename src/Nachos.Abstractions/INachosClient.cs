using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions;

/// <summary>
/// The M1 API surface, one method per route. Implemented in-process by Nachos.Core and over HTTP by Nachos.Client.
/// </summary>
/// <remarks>
/// <c>filters</c> is the Honcho filter body (<c>{"metadata":{...}}</c> and friends), or null for none. Invalid ids,
/// paging, or filters raise <see cref="RequestValidationException"/> or <see cref="NachosValidationException"/> (422);
/// a missing parent resource raises <see cref="NotFoundException"/> (404); authorization failures raise
/// <see cref="AuthException"/> (401). Every list method returns the <see cref="Page{T}"/> envelope.
/// </remarks>
public interface INachosClient
{
    /// <summary>Returns the workspace, creating it if missing. An existing workspace is returned unchanged (200 either way).</summary>
    /// <exception cref="NachosValidationException">The id does not match <c>^[a-zA-Z0-9_-]+$</c> or exceeds 512 characters.</exception>
    Task<Workspace> GetOrCreateWorkspaceAsync(
        string id,
        JsonObject? metadata = null,
        WorkspaceConfiguration? configuration = null,
        CancellationToken ct = default);

    /// <summary>Lists workspaces the caller may see, in creation order (reversed when <see cref="PageRequest.Reverse"/>).</summary>
    Task<Page<Workspace>> ListWorkspacesAsync(
        JsonObject? filters, PageRequest page, CancellationToken ct = default);

    /// <summary>Replaces the workspace's metadata and/or configuration; a null argument leaves that field unchanged.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<Workspace> UpdateWorkspaceAsync(
        string id,
        JsonObject? metadata = null,
        WorkspaceConfiguration? configuration = null,
        CancellationToken ct = default);

    /// <summary>Returns the peer, creating it if missing. An existing peer is returned unchanged.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<Peer> GetOrCreatePeerAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        JsonObject? configuration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lists peers in creation order. A null <paramref name="kind"/> means regular peers only
    /// (<see cref="PeerKind.Regular"/>); the wire values <c>"scope"</c> and <c>"all"</c> select the others.
    /// </summary>
    Task<Page<Peer>> ListPeersAsync(
        string workspaceId, PeerKind? kind, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    /// <summary>Replaces the peer's metadata and/or configuration; a null argument leaves that field unchanged.</summary>
    /// <exception cref="NotFoundException">The peer does not exist.</exception>
    Task<Peer> UpdatePeerAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        JsonObject? configuration = null,
        CancellationToken ct = default);

    /// <summary>Lists the sessions the peer is currently an active member of, in session creation order.</summary>
    Task<Page<Session>> ListPeerSessionsAsync(
        string workspaceId, string peerId, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    /// <summary>
    /// Returns the session, creating it if missing. An existing session is returned unchanged, but every peer in
    /// <paramref name="peers"/> is always ensured to be an active member with its config applied.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<Session> GetOrCreateSessionAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        SessionConfiguration? configuration = null,
        IReadOnlyDictionary<string, SessionPeerConfig>? peers = null,
        CancellationToken ct = default);

    /// <summary>Lists the workspace's sessions in creation order (reversed when <see cref="PageRequest.Reverse"/>).</summary>
    Task<Page<Session>> ListSessionsAsync(
        string workspaceId, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    /// <summary>Replaces the session's metadata and/or configuration; a null argument leaves that field unchanged.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Session> UpdateSessionAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        SessionConfiguration? configuration = null,
        CancellationToken ct = default);

    /// <summary>Adds the peers as active members (reactivating former members) and returns the session.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Session> AddSessionPeersAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct = default);

    /// <summary>Makes exactly the listed peers the active members, removing the rest, and returns the session.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Session> SetSessionPeersAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct = default);

    /// <summary>Removes the named peers from the session (naming a non-member is a no-op) and returns the session.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Session> RemoveSessionPeersAsync(
        string workspaceId, string sessionId, IReadOnlyList<string> peerIds, CancellationToken ct = default);

    /// <summary>
    /// Lists the session's active members. <see cref="PageRequest.Reverse"/> is ignored because the route accepts
    /// only <c>page</c> and <c>size</c>.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Page<Peer>> ListSessionPeersAsync(
        string workspaceId, string sessionId, PageRequest page, CancellationToken ct = default);

    /// <summary>Returns an active member's observation config.</summary>
    /// <exception cref="NotFoundException">The session does not exist, or the peer is not an active member.</exception>
    Task<SessionPeerConfig> GetSessionPeerConfigAsync(
        string workspaceId, string sessionId, string peerId, CancellationToken ct = default);

    /// <summary>Replaces an active member's observation config (204 on the wire).</summary>
    /// <exception cref="NotFoundException">The session does not exist, or the peer is not an active member.</exception>
    Task SetSessionPeerConfigAsync(
        string workspaceId,
        string sessionId,
        string peerId,
        SessionPeerConfig config,
        CancellationToken ct = default);

    /// <summary>
    /// Appends 1 to 100 messages, creating sender peers and adding them as members as needed. With an
    /// <paramref name="idempotencyKey"/>, a repeat of the same request replays the original response.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    /// <exception cref="IdempotencyKeyReusedException">The key was already used with a different request.</exception>
    Task<IReadOnlyList<Message>> CreateMessagesAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<MessageCreate> messages,
        string? idempotencyKey = null,
        CancellationToken ct = default);

    /// <summary>Lists messages by sequence (reversed when <see cref="PageRequest.Reverse"/>), never by timestamp.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Page<Message>> ListMessagesAsync(
        string workspaceId, string sessionId, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    /// <summary>Returns one message.</summary>
    /// <exception cref="NotFoundException">The session or the message does not exist.</exception>
    Task<Message> GetMessageAsync(
        string workspaceId, string sessionId, string messageId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the message's metadata. A null <paramref name="metadata"/> leaves metadata unchanged and returns the
    /// message as it is.
    /// </summary>
    /// <exception cref="NotFoundException">The session or the message does not exist.</exception>
    Task<Message> UpdateMessageAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        JsonObject? metadata,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a scoped API key (<c>POST /v3/keys</c>). Admin-only. <paramref name="peerId"/> requires
    /// <paramref name="workspaceId"/> and cannot be combined with <paramref name="sessionId"/>.
    /// </summary>
    /// <exception cref="AuthException">The caller is not an admin.</exception>
    /// <exception cref="RequestValidationException">The scope combination is invalid.</exception>
    Task<KeyResponse> CreateKeyAsync(
        string? workspaceId = null,
        string? peerId = null,
        string? sessionId = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Grants <paramref name="role"/> (see <see cref="GrantRoles"/>) to <paramref name="objectId"/>
    /// (<c>POST /v3/admin/grants</c>). Admin-only. A null <paramref name="workspaceId"/> means all workspaces.
    /// </summary>
    /// <exception cref="AuthException">The caller is not an admin.</exception>
    /// <exception cref="NotFoundException">The named workspace does not exist.</exception>
    Task AddGrantAsync(
        string objectId, string? workspaceId, string role, CancellationToken ct = default);
}