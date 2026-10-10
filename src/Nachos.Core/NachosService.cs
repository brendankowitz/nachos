using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Json;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;

namespace Nachos.Core;

/// <summary>
/// Trusted in-process M1 operations. Transport authentication and authorization belong to the host;
/// validation and workspace-scoped storage calls apply to every caller.
/// </summary>
public sealed partial class NachosService(
    IMemoryStore store,
    ITokenCounter tokenCounter,
    RequestValidator validator,
    IKeyIssuer keyIssuer) : INachosClient
{
    public async Task<Workspace> GetOrCreateWorkspaceAsync(string id, JsonObject? metadata = null,
        WorkspaceConfiguration? configuration = null, CancellationToken ct = default)
    {
        Validate(ct, (id, "id"));
        var data = Canonical(metadata);
        var config = ConfigurationJson.Project(configuration);
        validator.ValidateWorkspaceConfiguration(configuration);
        return Map(await store.Workspaces.GetOrCreateAsync(id, data, config, ct).ConfigureAwait(false));
    }

    public async Task<Page<Workspace>> ListWorkspacesAsync(JsonObject? filters, PageRequest page, CancellationToken ct = default)
    {
        Validate(ct);
        ArgumentNullException.ThrowIfNull(page);
        var filter = FilterParser.Parse(filters, ResourceKind.Workspace);
        return MapPage(await store.Workspaces.ListAsync(filter, page, ct).ConfigureAwait(false), Map);
    }

    public async Task<Workspace> UpdateWorkspaceAsync(string id, JsonObject? metadata = null,
        WorkspaceConfiguration? configuration = null, CancellationToken ct = default)
    {
        Validate(ct, (id, "id"));
        var data = Canonical(metadata);
        var config = ConfigurationJson.Project(configuration);
        validator.ValidateWorkspaceConfiguration(configuration);
        return Map(await store.Workspaces.UpdateAsync(id, data, config, ct).ConfigureAwait(false));
    }

    public async Task<Peer> GetOrCreatePeerAsync(string workspaceId, string id, JsonObject? metadata = null,
        JsonObject? configuration = null, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (id, "id"));
        var data = Canonical(metadata);
        var config = Canonical(configuration);
        return Map(await store.Peers.GetOrCreateAsync(workspaceId, id, data, config, ct).ConfigureAwait(false));
    }

    public async Task<Page<Peer>> ListPeersAsync(string workspaceId, PeerKind? kind, JsonObject? filters,
        PageRequest page, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        ArgumentNullException.ThrowIfNull(page);
        var selectedKind = kind ?? PeerKind.Regular;
        if (!Enum.IsDefined(selectedKind))
        {
            throw new NachosValidationException("Peer kind must be regular, scope or all.");
        }
        var filter = FilterParser.Parse(filters, ResourceKind.Peer);
        return MapPage(await store.Peers.ListAsync(workspaceId, selectedKind, filter, page, ct).ConfigureAwait(false), Map);
    }

    public async Task<Peer> UpdatePeerAsync(string workspaceId, string id, JsonObject? metadata = null,
        JsonObject? configuration = null, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (id, "id"));
        var data = Canonical(metadata);
        var config = Canonical(configuration);
        return Map(await store.Peers.UpdateAsync(workspaceId, id, data, config, ct).ConfigureAwait(false));
    }

    public async Task<Page<Session>> ListPeerSessionsAsync(string workspaceId, string peerId, JsonObject? filters,
        PageRequest page, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (peerId, "peer_id"));
        ArgumentNullException.ThrowIfNull(page);
        var filter = FilterParser.Parse(filters, ResourceKind.Session);
        return MapPage(await store.Peers.ListSessionsForPeerAsync(workspaceId, peerId, filter, page, ct).ConfigureAwait(false), Map);
    }

    public async Task<Session> GetOrCreateSessionAsync(string workspaceId, string id, JsonObject? metadata = null,
        SessionConfiguration? configuration = null, IReadOnlyDictionary<string, SessionPeerConfig>? peers = null,
        CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (id, "id"));
        var data = Canonical(metadata);
        var config = ConfigurationJson.Project(configuration);
        validator.ValidateSessionConfiguration(configuration);
        var members = peers is null ? null : ReadMembers(peers);
        return Map(await store.Sessions.GetOrCreateAsync(workspaceId, id, data, config, members, ct).ConfigureAwait(false));
    }

    public async Task<Page<Session>> ListSessionsAsync(string workspaceId, JsonObject? filters, PageRequest page,
        CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        ArgumentNullException.ThrowIfNull(page);
        var filter = FilterParser.Parse(filters, ResourceKind.Session);
        return MapPage(await store.Sessions.ListAsync(workspaceId, filter, page, ct).ConfigureAwait(false), Map);
    }

    public async Task<Session> UpdateSessionAsync(string workspaceId, string id, JsonObject? metadata = null,
        SessionConfiguration? configuration = null, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (id, "id"));
        var data = Canonical(metadata);
        var config = ConfigurationJson.Project(configuration);
        validator.ValidateSessionConfiguration(configuration);
        return Map(await store.Sessions.UpdateAsync(workspaceId, id, data, config, ct).ConfigureAwait(false));
    }

    public async Task<Session> AddSessionPeersAsync(string workspaceId, string sessionId,
        IReadOnlyDictionary<string, SessionPeerConfig> peers, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        var members = ReadMembers(peers);
        await store.Sessions.AddPeersAsync(workspaceId, sessionId, members, ct).ConfigureAwait(false);
        return Map(await RequireSession(workspaceId, sessionId, ct).ConfigureAwait(false));
    }

    public async Task<Session> SetSessionPeersAsync(string workspaceId, string sessionId,
        IReadOnlyDictionary<string, SessionPeerConfig> peers, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        var members = ReadMembers(peers);
        await store.Sessions.SetPeersAsync(workspaceId, sessionId, members, ct).ConfigureAwait(false);
        return Map(await RequireSession(workspaceId, sessionId, ct).ConfigureAwait(false));
    }

    public async Task<Session> RemoveSessionPeersAsync(string workspaceId, string sessionId,
        IReadOnlyList<string> peerIds, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        ArgumentNullException.ThrowIfNull(peerIds);
        var peers = peerIds.ToArray();
        foreach (var peer in peers)
        {
            IdValidator.Validate(peer, "peer_id");
        }
        await store.Sessions.RemovePeersAsync(workspaceId, sessionId, peers, ct).ConfigureAwait(false);
        return Map(await RequireSession(workspaceId, sessionId, ct).ConfigureAwait(false));
    }

    public async Task<Page<Peer>> ListSessionPeersAsync(string workspaceId, string sessionId, PageRequest page,
        CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        ArgumentNullException.ThrowIfNull(page);
        return MapPage(await store.Sessions.ListPeersAsync(workspaceId, sessionId, page with { Reverse = false }, ct)
            .ConfigureAwait(false), Map);
    }

    public Task<SessionPeerConfig> GetSessionPeerConfigAsync(string workspaceId, string sessionId, string peerId,
        CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"), (peerId, "peer_id"));
        return store.Sessions.GetPeerConfigAsync(workspaceId, sessionId, peerId, ct);
    }

    public Task SetSessionPeerConfigAsync(string workspaceId, string sessionId, string peerId,
        SessionPeerConfig config, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"), (peerId, "peer_id"));
        ArgumentNullException.ThrowIfNull(config);
        return store.Sessions.SetPeerConfigAsync(workspaceId, sessionId, peerId, config, ct);
    }

    public async Task<Page<Message>> ListMessagesAsync(string workspaceId, string sessionId, JsonObject? filters,
        PageRequest page, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        ArgumentNullException.ThrowIfNull(page);
        var filter = FilterParser.Parse(filters, ResourceKind.Message);
        return MapPage(await store.Messages.ListAsync(workspaceId, sessionId, filter, page, ct).ConfigureAwait(false), Map);
    }

    public async Task<Message> GetMessageAsync(string workspaceId, string sessionId, string messageId,
        CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"), (messageId, "message_id"));
        return Map(await store.Messages.GetAsync(workspaceId, sessionId, messageId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Message not found."));
    }

    public async Task<Message> UpdateMessageAsync(string workspaceId, string sessionId, string messageId,
        JsonObject? metadata, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"), (messageId, "message_id"));
        var data = Canonical(metadata);
        return data is null
            ? await GetMessageAsync(workspaceId, sessionId, messageId, ct).ConfigureAwait(false)
            : Map(await store.Messages.UpdateMetadataAsync(workspaceId, sessionId, messageId, data, ct).ConfigureAwait(false));
    }

    public Task<KeyResponse> CreateKeyAsync(string? workspaceId = null, string? peerId = null,
        string? sessionId = null, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (workspaceId is null || (peerId is not null && sessionId is not null))
        {
            throw new NachosValidationException("A workspace scope is required; peer and session scopes cannot be combined.");
        }
        IdValidator.Validate(workspaceId, "workspace_id");
        if (peerId is not null)
        {
            IdValidator.Validate(peerId, "peer_id");
        }
        if (sessionId is not null)
        {
            IdValidator.Validate(sessionId, "session_id");
        }
        return Task.FromResult(new KeyResponse(keyIssuer.Issue(new(false, workspaceId, peerId, sessionId, expiresAt))));
    }

    public Task AddGrantAsync(string objectId, string? workspaceId, string role, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(objectId) || role is not (GrantRoles.Admin or GrantRoles.Workspace))
        {
            throw new NachosValidationException("A nonempty object_id and a supported Nachos role are required.");
        }
        if (workspaceId is not null)
        {
            IdValidator.Validate(workspaceId, "workspace_id");
        }
        return store.Grants.AddAsync(new(objectId, workspaceId, role), ct);
    }

    private async Task<SessionRecord> RequireSession(string workspace, string session, CancellationToken ct) =>
        await store.Sessions.GetAsync(workspace, session, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Session not found.");

    private static void Validate(CancellationToken ct, params (string Value, string Name)[] ids)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var (value, name) in ids)
        {
            IdValidator.Validate(value, name);
        }
    }

    private static Dictionary<string, SessionPeerConfig> ReadMembers(IReadOnlyDictionary<string, SessionPeerConfig> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var result = new Dictionary<string, SessionPeerConfig>(StringComparer.Ordinal);
        foreach (var (id, config) in peers)
        {
            IdValidator.Validate(id, "peer_id");
            if (config is null)
            {
                throw new NachosValidationException("A peer configuration must be an object.");
            }
            result.Add(id, config);
        }
        return result;
    }

    private static JsonObject? Canonical(JsonObject? value) => StrictJsonData.ToCanonical(value)?.AsObject();

    private static Workspace Map(WorkspaceRecord row) => new(row.Name, row.Metadata, row.Configuration, row.CreatedAt);
    private static Peer Map(PeerRecord row) => new(row.Name, row.WorkspaceName, row.CreatedAt, row.Metadata, row.Configuration);
    private static Session Map(SessionRecord row) => new(row.Name, row.State == LifecycleState.Active,
        row.WorkspaceName, row.Metadata, row.Configuration, row.CreatedAt);
    private static Message Map(MessageRecord row) => new(row.PublicId, row.Content, row.PeerName, row.SessionName,
        row.Metadata, row.CreatedAt, row.WorkspaceName, row.TokenCount);
    private static Page<TOut> MapPage<TIn, TOut>(Page<TIn> page, Func<TIn, TOut> map) =>
        new(page.Items.Select(map).ToArray(), page.Total, page.PageNumber, page.Size, page.Pages);
}
