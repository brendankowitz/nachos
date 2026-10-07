using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

internal sealed class InMemoryPeerStore(InMemoryState state) : IPeerStore
{
    public Task<PeerRecord> GetOrCreateAsync(
        string workspaceName,
        string name,
        JsonObject? metadata,
        JsonObject? configuration,
        CancellationToken ct,
        bool isInternal = false) =>
        StoreTask.Run(
            () =>
            {
                var ownedMetadata = JsonCopy.Own(metadata, "metadata");
                var ownedConfiguration = JsonCopy.Own(configuration, "configuration");
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    if (!workspace.Peers.TryGetValue(name, out var peer))
                    {
                        var record = new PeerRecord(
                            workspaceName, name, ownedMetadata, ownedConfiguration, isInternal, state.Clock.GetUtcNow());
                        peer = new PeerEntry(record, state.NextOrder());
                        workspace.Peers.Add(name, peer);
                    }

                    return JsonCopy.Out(peer.Record);
                }
            },
            ct);

    public Task<PeerRecord?> GetAsync(string workspaceName, string name, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                if (state.FindWorkspace(workspaceName) is not { } workspace)
                {
                    return null;
                }

                lock (workspace.Gate)
                {
                    return workspace.Peers.TryGetValue(name, out var peer) ? JsonCopy.Out(peer.Record) : null;
                }
            },
            ct);

    public Task<PeerRecord> UpdateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var newMetadata = JsonCopy.OwnOptional(metadata, "metadata");
                var newConfiguration = JsonCopy.OwnOptional(configuration, "configuration");
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var peer = workspace.RequirePeer(name);
                    peer = peer with
                    {
                        Record = peer.Record with
                        {
                            Metadata = newMetadata ?? peer.Record.Metadata,
                            Configuration = newConfiguration ?? peer.Record.Configuration,
                        },
                    };
                    workspace.Peers[name] = peer;
                    return JsonCopy.Out(peer.Record);
                }
            },
            ct);

    public Task<Page<PeerRecord>> ListAsync(
        string workspaceName, PeerKind kind, FilterNode? filter, PageRequest page, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var rows = workspace.Peers.Values
                        .Where(peer => kind switch
                        {
                            PeerKind.Regular => !peer.Record.IsInternal,
                            PeerKind.Scope => peer.Record.IsInternal,
                            _ => true,
                        })
                        .Where(peer => InMemoryFilterEvaluator.Matches(filter, peer.Record))
                        .OrderBy(peer => peer.Record.CreatedAt)
                        .ThenBy(peer => peer.Order);
                    return Paging.ToPage(rows, page, peer => JsonCopy.Out(peer.Record));
                }
            },
            ct);

    public Task<Page<SessionRecord>> ListSessionsForPeerAsync(
        string workspaceName, string peerName, FilterNode? filter, PageRequest page, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    workspace.RequirePeer(peerName);
                    var rows = workspace.Sessions.Values
                        .Where(session => session.IsActiveMember(peerName) && session.Matches(filter))
                        .OrderBy(session => session.Record.CreatedAt)
                        .ThenBy(session => session.Order);
                    return Paging.ToPage(rows, page, session => JsonCopy.Out(session.Record));
                }
            },
            ct);
}
