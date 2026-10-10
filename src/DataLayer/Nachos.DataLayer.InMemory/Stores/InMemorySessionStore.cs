using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

internal sealed class InMemorySessionStore(InMemoryState state) : ISessionStore
{
    public Task<SessionRecord> GetOrCreateAsync(
        string workspaceName,
        string name,
        JsonObject? metadata,
        JsonObject? configuration,
        IReadOnlyDictionary<string, SessionPeerConfig>? peers,
        CancellationToken ct) =>
        state.Run(
            () =>
            {
                var ownedMetadata = JsonCopy.Own(metadata, "metadata");
                var ownedConfiguration = JsonCopy.Own(configuration, "configuration");
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var now = state.Clock.GetUtcNow();
                    if (!workspace.Sessions.TryGetValue(name, out var session))
                    {
                        var record = new SessionRecord(
                            workspaceName, name, LifecycleState.Active, ownedMetadata, ownedConfiguration, now);
                        session = new SessionEntry(record, state.NextOrder());
                        workspace.Sessions.Add(name, session);
                    }

                    if (peers is not null)
                    {
                        ActivatePeers(workspace, session, peers, now);
                    }

                    return JsonCopy.Out(session.Record);
                }
            },
            ct);

    public Task<SessionRecord?> GetAsync(string workspaceName, string name, CancellationToken ct) =>
        state.Run(
            () =>
            {
                if (state.FindWorkspace(workspaceName) is not { } workspace)
                {
                    return null;
                }

                lock (workspace.Gate)
                {
                    return workspace.Sessions.TryGetValue(name, out var session) ? JsonCopy.Out(session.Record) : null;
                }
            },
            ct);

    public Task<SessionRecord> UpdateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var newMetadata = JsonCopy.OwnOptional(metadata, "metadata");
                var newConfiguration = JsonCopy.OwnOptional(configuration, "configuration");
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var session = workspace.RequireSession(name);
                    session.Record = session.Record with
                    {
                        Metadata = newMetadata ?? session.Record.Metadata,
                        Configuration = newConfiguration ?? session.Record.Configuration,
                    };
                    return JsonCopy.Out(session.Record);
                }
            },
            ct);

    public Task<Page<SessionRecord>> ListAsync(
        string workspaceName, FilterNode? filter, PageRequest page, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                var prepared = InMemoryFilterEvaluator.Prepare(filter);
                lock (workspace.Gate)
                {
                    var rows = workspace.Sessions.Values
                        .Where(session => session.Matches(prepared))
                        .OrderBy(session => session.Record.CreatedAt)
                        .ThenBy(session => session.Order);
                    return Paging.ToPage(rows, page, session => JsonCopy.Out(session.Record));
                }
            },
            ct);

    public Task AddPeersAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct) =>
        state.Run(
            () => WithSession(workspaceName, sessionName, (workspace, session, now) =>
                ActivatePeers(workspace, session, peers, now)),
            ct);

    public Task SetPeersAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct) =>
        state.Run(
            () => WithSession(workspaceName, sessionName, (workspace, session, now) =>
            {
                // Ordinal, whatever comparer the caller's dictionary uses.
                var listed = peers.Keys.ToHashSet(StringComparer.Ordinal);
                foreach (var unlisted in session.ActiveMemberNames.Where(name => !listed.Contains(name)).ToList())
                {
                    session.Leave(unlisted, now);
                }

                ActivatePeers(workspace, session, peers, now);
            }),
            ct);

    public Task RemovePeersAsync(
        string workspaceName, string sessionName, IReadOnlyList<string> peerNames, CancellationToken ct) =>
        state.Run(
            () => WithSession(workspaceName, sessionName, (_, session, now) =>
            {
                foreach (var peerName in peerNames)
                {
                    session.Leave(peerName, now);
                }
            }),
            ct);

    public Task<Page<PeerRecord>> ListPeersAsync(
        string workspaceName, string sessionName, PageRequest page, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var rows = workspace.RequireSession(sessionName).ActiveMemberNames
                        .Select(name => workspace.Peers[name])
                        .OrderBy(peer => peer.Record.CreatedAt)
                        .ThenBy(peer => peer.Order);
                    return Paging.ToPage(rows, page, peer => JsonCopy.Out(peer.Record));
                }
            },
            ct);

    public Task<SessionPeerConfig> GetPeerConfigAsync(
        string workspaceName, string sessionName, string peerName, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return RequireActiveMember(workspace.RequireSession(sessionName), peerName).Config;
                }
            },
            ct);

    public Task SetPeerConfigAsync(
        string workspaceName, string sessionName, string peerName, SessionPeerConfig config, CancellationToken ct) =>
        state.Run(
            () => WithSession(workspaceName, sessionName, (_, session, _) =>
                session.Members[peerName] = RequireActiveMember(session, peerName) with { Config = config }),
            ct);

    public Task<bool> IsActiveMemberAsync(
        string workspaceName, string sessionName, string peerName, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return workspace.RequireSession(sessionName).IsActiveMember(peerName);
                }
            },
            ct);

    /// <summary>Runs a mutation on an existing session under its workspace's gate.</summary>
    private void WithSession(
        string workspaceName, string sessionName, Action<WorkspaceEntry, SessionEntry, DateTimeOffset> mutate)
    {
        var workspace = state.RequireWorkspace(workspaceName);
        lock (workspace.Gate)
        {
            mutate(workspace, workspace.RequireSession(sessionName), state.Clock.GetUtcNow());
        }
    }

    /// <summary>Creates missing peers and makes each listed peer an active member with its given config.</summary>
    private void ActivatePeers(
        WorkspaceEntry workspace,
        SessionEntry session,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        DateTimeOffset now)
    {
        foreach (var (peerName, config) in peers)
        {
            workspace.GetOrAddPeer(peerName, now, state.NextOrder);
            session.Activate(peerName, config, now);
        }
    }

    private static Membership RequireActiveMember(SessionEntry session, string peerName) =>
        session.Members.GetValueOrDefault(peerName) is { IsActive: true } membership
            ? membership
            : throw new NotFoundException($"Peer '{peerName}' is not an active member of session '{session.Record.Name}'.");
}
