using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions;

/// <summary>
/// The API surface, one method per route. Implemented in-process by Nachos.Core and over HTTP by Nachos.Client.
/// </summary>
/// <remarks>
/// <c>filters</c> is the Honcho filter body (<c>{"metadata":{...}}</c> and friends), or null for none. Filters, metadata
/// and configuration must be strict JSON data: values outside the types listed on <see cref="Json.StrictJsonData"/> are a
/// 422. Data already converted by the caller is treated as data and cannot prove its earlier CLR source was well
/// formed: <c>JsonSerializer.SerializeToNode</c> has already turned a lone surrogate into U+FFFD, so callers who need
/// that guarantee must build <c>JsonObject</c>/<c>JsonArray</c> with literal strings. Invalid ids,
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
    /// The request identity is the documented JSON projection of <paramref name="messages"/>, hashed with the
    /// method and canonical route, and it shares one Core path with the HTTP adapter. A replay returns the
    /// response captured inside the original append transaction.
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
    /// Issues a scoped API key (<c>POST /v3/keys</c>). Admin-only. At least one of
    /// <paramref name="workspaceId"/>, <paramref name="peerId"/> or <paramref name="sessionId"/> is required. An
    /// absent scope never mints an admin key. <paramref name="peerId"/> and <paramref name="sessionId"/> each
    /// require <paramref name="workspaceId"/>, and they cannot be combined.
    /// </summary>
    /// <exception cref="AuthException">The caller is not an admin.</exception>
    /// <exception cref="NachosValidationException">
    /// The scope combination is invalid, or signing keys are not configured. A secret is never invented.
    /// </exception>
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

    /// <summary>
    /// Creates 1 to 100 explicit conclusions (<c>POST C</c>, 201), all or nothing and without deduplication. They have
    /// no source ids, and their embeddings are computed in the background if not immediately.
    /// </summary>
    /// <exception cref="RequestValidationException">The batch or a content length is out of bounds.</exception>
    /// <exception cref="NotFoundException">The workspace, a named peer or a named session does not exist.</exception>
    Task<IReadOnlyList<Conclusion>> CreateConclusionsAsync(
        string workspaceId, IReadOnlyList<ConclusionCreate> conclusions, CancellationToken ct = default);

    /// <summary>Lists the workspace's conclusions in creation order (reversed when <see cref="PageRequest.Reverse"/>).</summary>
    Task<Page<Conclusion>> ListConclusionsAsync(
        string workspaceId, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    /// <summary>
    /// The conclusions nearest to the query text, nearest first. Conclusions still waiting for their embedding are not
    /// found.
    /// </summary>
    /// <exception cref="RequestValidationException"><c>top_k</c> or <c>distance</c> is out of bounds.</exception>
    Task<IReadOnlyList<Conclusion>> QueryConclusionsAsync(
        string workspaceId, ConclusionQuery query, CancellationToken ct = default);

    /// <summary>Returns one conclusion.</summary>
    /// <exception cref="NotFoundException">The conclusion does not exist or was deleted.</exception>
    Task<Conclusion> GetConclusionAsync(string workspaceId, string conclusionId, CancellationToken ct = default);

    /// <summary>Deletes the conclusion (204 on the wire). Deleting a missing conclusion is a no-op.</summary>
    Task DeleteConclusionAsync(string workspaceId, string conclusionId, CancellationToken ct = default);

    /// <summary>The card <paramref name="peerId"/> keeps about <paramref name="target"/> (itself when null).</summary>
    Task<PeerCardResponse> GetPeerCardAsync(
        string workspaceId, string peerId, string? target = null, CancellationToken ct = default);

    /// <summary>
    /// Replaces the card <paramref name="peerId"/> keeps about <paramref name="target"/> (itself when null) with
    /// <paramref name="peerCard"/>, stored as sent.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace or either peer does not exist.</exception>
    Task<PeerCardResponse> SetPeerCardAsync(
        string workspaceId,
        string peerId,
        IReadOnlyList<string> peerCard,
        string? target = null,
        CancellationToken ct = default);

    /// <summary>
    /// The working representation <paramref name="peerId"/> holds of its target: recent, optionally most frequent and
    /// optionally semantically matched conclusions, as Markdown sections by level. No model is called.
    /// </summary>
    /// <exception cref="RequestValidationException">A bound is violated.</exception>
    /// <exception cref="NachosValidationException"><c>scope</c> is set; scopes are not supported in this version.</exception>
    Task<RepresentationResponse> GetRepresentationAsync(
        string workspaceId, string peerId, PeerRepresentationGet request, CancellationToken ct = default);

    /// <summary>The representation and peer card <paramref name="peerId"/> holds of its target (<c>GET P/context</c>).</summary>
    /// <exception cref="RequestValidationException">A bound is violated.</exception>
    Task<PeerContext> GetPeerContextAsync(
        string workspaceId, string peerId, PeerContextQuery query, CancellationToken ct = default);

    /// <summary>
    /// Searches the messages of every session of the workspace (<c>POST W/search</c>): vector and lexical matches fused,
    /// best first. A blank query finds nothing.
    /// </summary>
    /// <exception cref="RequestValidationException"><c>limit</c> is out of bounds or a filter is invalid.</exception>
    /// <exception cref="NachosValidationException"><c>scope</c> is set; scopes are not supported in this version.</exception>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<IReadOnlyList<Message>> SearchWorkspaceAsync(
        string workspaceId, WorkspaceMessageSearchOptions options, CancellationToken ct = default);

    /// <summary>Searches one session's messages (<c>POST S/search</c>); a session that does not exist matches nothing.</summary>
    /// <exception cref="RequestValidationException"><c>limit</c> is out of bounds or a filter is invalid.</exception>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<IReadOnlyList<Message>> SearchSessionAsync(
        string workspaceId, string sessionId, MessageSearchOptions options, CancellationToken ct = default);

    /// <summary>
    /// Searches the messages the peer could have seen (<c>POST P/search</c>): those of its sessions sent while it was a
    /// member. A peer that does not exist matches nothing.
    /// </summary>
    /// <exception cref="RequestValidationException"><c>limit</c> is out of bounds or a filter is invalid.</exception>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<IReadOnlyList<Message>> SearchPeerAsync(
        string workspaceId, string peerId, MessageSearchOptions options, CancellationToken ct = default);

    /// <summary>
    /// The session's context (<c>GET S/context</c>): its newest messages, summary and, with a <c>peer_target</c>, a
    /// representation and card, never more than <see cref="SessionContextQuery.Tokens"/> content tokens.
    /// </summary>
    /// <exception cref="RequestValidationException">A bound is exceeded or the parameters do not combine.</exception>
    /// <exception cref="NachosValidationException">An id is invalid, or <c>scope</c> is set; scopes are not supported in this version.</exception>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    Task<SessionContext> GetSessionContextAsync(
        string workspaceId, string sessionId, SessionContextQuery query, CancellationToken ct = default);

    /// <summary>The session's current short and long summaries (<c>GET S/summaries</c>); each is null until one exists.</summary>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    Task<SessionSummaries> GetSessionSummariesAsync(string workspaceId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Accepts the session's deletion (<c>DELETE S</c>, 202 with no body): it is inactive at once, every later write
    /// under it is a 409, and a job (see <see cref="ListJobsAsync"/>) removes it with its messages, summaries and
    /// session-attributed conclusions. Repeating it while the job runs is harmless and names the same job.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    /// <exception cref="ConflictException">The workspace is being deleted.</exception>
    Task DeleteSessionAsync(string workspaceId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Accepts the workspace's deletion (<c>DELETE W</c>, 202 with no body): every later write under it is a 409, and a
    /// job removes it with everything in it. Repeating it while the job runs is harmless and names the same job.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    /// <exception cref="ConflictException">The workspace has an active session; delete its sessions first.</exception>
    Task DeleteWorkspaceAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>
    /// The workspace's background jobs (<c>GET W/jobs</c>, a Nachos extension), oldest first (newest first when
    /// <see cref="PageRequest.Reverse"/>). In M3 these are its deletions and its sessions'.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<Page<Job>> ListJobsAsync(string workspaceId, PageRequest page, CancellationToken ct = default);

    /// <summary>
    /// Counts the workspace's reasoning work, optionally only that observed by <paramref name="observerId"/>, sent by
    /// <paramref name="senderId"/> or in <paramref name="sessionId"/>.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<QueueStatus> GetQueueStatusAsync(
        string workspaceId,
        string? observerId = null,
        string? senderId = null,
        string? sessionId = null,
        CancellationToken ct = default);
}
