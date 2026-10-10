using Nachos.Abstractions;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>A stored workspace and everything scoped to it.</summary>
/// <remarks>
/// <see cref="Record"/> is guarded by <see cref="InMemoryState.Gate"/>; every other mutable member is guarded by
/// <see cref="Gate"/>.
/// </remarks>
internal sealed class WorkspaceEntry(WorkspaceRecord record, long order)
{
    public string Name { get; } = record.Name;

    public long Order { get; } = order;

    public WorkspaceRecord Record { get; set; } = record;

    public Lock Gate { get; } = new();

    public Dictionary<string, PeerEntry> Peers { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SessionEntry> Sessions { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IdempotencyRecord> Idempotency { get; } = new(StringComparer.Ordinal);

    /// <exception cref="NotFoundException">The peer does not exist.</exception>
    public PeerEntry RequirePeer(string name) =>
        Peers.GetValueOrDefault(name) ?? throw new NotFoundException($"Peer '{name}' not found in workspace '{Name}'.");

    /// <exception cref="NotFoundException">The session does not exist.</exception>
    public SessionEntry RequireSession(string name) =>
        Sessions.GetValueOrDefault(name)
        ?? throw new NotFoundException($"Session '{name}' not found in workspace '{Name}'.");

    /// <summary>Returns the peer, creating a regular peer with empty metadata and configuration if missing.</summary>
    public PeerEntry GetOrAddPeer(string name, DateTimeOffset now, Func<long> nextOrder)
    {
        if (!Peers.TryGetValue(name, out var peer))
        {
            peer = PeerEntry.CreateDefault(Name, name, now, nextOrder());
            Peers.Add(name, peer);
        }

        return peer;
    }
}
