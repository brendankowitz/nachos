using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions;

/// <summary>
/// The M1 API surface, one method per route. Implemented in-process by Nachos.Core and over HTTP by Nachos.Client.
/// <c>filters</c> is the Honcho filter body (<c>{"metadata":{...}}</c> and friends), or null for none.
/// </summary>
public interface INachosClient
{
    Task<Workspace> GetOrCreateWorkspaceAsync(
        string id,
        JsonObject? metadata = null,
        WorkspaceConfiguration? configuration = null,
        CancellationToken ct = default);

    Task<Page<Workspace>> ListWorkspacesAsync(
        JsonObject? filters, PageRequest page, CancellationToken ct = default);

    Task<Workspace> UpdateWorkspaceAsync(
        string id,
        JsonObject? metadata = null,
        WorkspaceConfiguration? configuration = null,
        CancellationToken ct = default);

    Task<Peer> GetOrCreatePeerAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        JsonObject? configuration = null,
        CancellationToken ct = default);

    Task<Page<Peer>> ListPeersAsync(
        string workspaceId, PeerKind? kind, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    Task<Peer> UpdatePeerAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        JsonObject? configuration = null,
        CancellationToken ct = default);

    Task<Page<Session>> ListPeerSessionsAsync(
        string workspaceId, string peerId, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    Task<Session> GetOrCreateSessionAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        SessionConfiguration? configuration = null,
        IReadOnlyDictionary<string, SessionPeerConfig>? peers = null,
        CancellationToken ct = default);

    Task<Page<Session>> ListSessionsAsync(
        string workspaceId, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    Task<Session> UpdateSessionAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        SessionConfiguration? configuration = null,
        CancellationToken ct = default);

    Task<Session> AddSessionPeersAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct = default);

    Task<Session> SetSessionPeersAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct = default);

    Task<Session> RemoveSessionPeersAsync(
        string workspaceId, string sessionId, IReadOnlyList<string> peerIds, CancellationToken ct = default);

    Task<Page<Peer>> ListSessionPeersAsync(
        string workspaceId, string sessionId, PageRequest page, CancellationToken ct = default);

    Task<SessionPeerConfig> GetSessionPeerConfigAsync(
        string workspaceId, string sessionId, string peerId, CancellationToken ct = default);

    Task SetSessionPeerConfigAsync(
        string workspaceId,
        string sessionId,
        string peerId,
        SessionPeerConfig config,
        CancellationToken ct = default);

    Task<IReadOnlyList<Message>> CreateMessagesAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<MessageCreate> messages,
        string? idempotencyKey = null,
        CancellationToken ct = default);

    Task<Page<Message>> ListMessagesAsync(
        string workspaceId, string sessionId, JsonObject? filters, PageRequest page, CancellationToken ct = default);

    Task<Message> GetMessageAsync(
        string workspaceId, string sessionId, string messageId, CancellationToken ct = default);

    Task<Message> UpdateMessageAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        JsonObject? metadata,
        CancellationToken ct = default);

    Task<KeyResponse> CreateKeyAsync(
        string? workspaceId = null,
        string? peerId = null,
        string? sessionId = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);
}