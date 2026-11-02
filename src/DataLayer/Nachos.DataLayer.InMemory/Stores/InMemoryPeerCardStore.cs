using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

internal sealed class InMemoryPeerCardStore(InMemoryState state) : IPeerCardStore
{
    public Task<PeerCardRecord?> GetAsync(string workspaceName, string observerName, string observedName, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return workspace.PeerCards.TryGetValue((observerName, observedName), out var card) ? Out(card) : null;
                }
            },
            ct);

    public Task<PeerCardRecord> SetAsync(
        string workspaceName,
        string observerName,
        string observedName,
        IReadOnlyList<string> entries,
        PeerCardAuthor updatedBy,
        CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(entries);
                string[] owned = [.. entries];
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    workspace.RequirePeer(observerName);
                    workspace.RequirePeer(observedName);
                    var card = new PeerCardRecord(
                        workspaceName, observerName, observedName, owned, state.Clock.GetUtcNow(), updatedBy);
                    workspace.PeerCards[(observerName, observedName)] = card;
                    return Out(card);
                }
            },
            ct);

    private static PeerCardRecord Out(PeerCardRecord card) => card with { Entries = [.. card.Entries] };
}
