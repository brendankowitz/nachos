using Nachos.Abstractions;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>A stored workspace and everything scoped to it.</summary>
/// <remarks>
/// <see cref="Record"/> is guarded by <see cref="InMemoryState.Gate"/>; every other mutable member is guarded by
/// <see cref="Gate"/>. The deletion transition (<see cref="MarkDeleting"/>) holds both, so the workspace's own writes,
/// under <see cref="Gate"/>, read <see cref="IsDeleting"/>, and the workspace-row writes, under
/// <see cref="InMemoryState.Gate"/>, read <c>Record.State</c>.
/// </remarks>
internal sealed class WorkspaceEntry(WorkspaceRecord record, long order)
{
    public string Name { get; } = record.Name;

    public long Order { get; } = order;

    public WorkspaceRecord Record { get; set; } = record;

    public Lock Gate { get; } = new();

    /// <summary>The work item id of the workspace's deletion; set exactly while it is being deleted.</summary>
    public long? DeletionJobId { get; private set; }

    /// <summary>
    /// True from deletion acceptance on. A removed workspace stays deleting, so an operation that found it just before
    /// its removal and writes afterwards is rejected instead of writing into a detached entry.
    /// </summary>
    public bool IsDeleting => DeletionJobId is not null;

    /// <summary>The caller holds <see cref="Gate"/> and <see cref="InMemoryState.Gate"/>.</summary>
    public void MarkDeleting(long deletionJobId)
    {
        DeletionJobId = deletionJobId;
        Record = Record with { State = LifecycleState.Deleting };
    }

    /// <exception cref="ConflictException">The workspace is being deleted.</exception>
    public void RequireNotDeleting()
    {
        if (IsDeleting)
        {
            throw new ConflictException("workspace is being deleted");
        }
    }

    /// <summary>The session, for a write under it.</summary>
    /// <exception cref="ConflictException">The workspace or the session is being deleted.</exception>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    public SessionEntry RequireWritableSession(string name)
    {
        RequireNotDeleting();
        var session = RequireSession(name);
        session.RequireNotDeleting();
        return session;
    }

    public Dictionary<string, PeerEntry> Peers { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SessionEntry> Sessions { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IdempotencyRecord> Idempotency { get; } = new(StringComparer.Ordinal);

    /// <summary>Conclusions by public id, live and tombstoned.</summary>
    public Dictionary<string, StoredConclusion> Conclusions { get; } = new(StringComparer.Ordinal);

    /// <summary>Collections by (observer, observed); string tuples compare ordinally.</summary>
    public Dictionary<(string Observer, string Observed), CollectionRecord> Collections { get; } = [];

    /// <summary>Peer cards by (observer, observed). Entries are store-owned arrays.</summary>
    public Dictionary<(string Observer, string Observed), PeerCardRecord> PeerCards { get; } = [];

    /// <exception cref="NotFoundException">The peer does not exist.</exception>
    public PeerEntry RequirePeer(string name) =>
        Peers.GetValueOrDefault(name) ?? throw new NotFoundException($"Peer '{name}' not found in workspace '{Name}'.");

    /// <exception cref="NotFoundException">The session does not exist.</exception>
    public SessionEntry RequireSession(string name) =>
        Sessions.GetValueOrDefault(name)
        ?? throw new NotFoundException($"Session '{name}' not found in workspace '{Name}'.");

    /// <summary>The session holding the message, or null. Linear in the sessions: message ids are unique per session here.</summary>
    public SessionEntry? FindSessionOfMessage(string messageId) =>
        Sessions.Values.FirstOrDefault(session => session.ContainsMessage(messageId));

    /// <exception cref="NotFoundException">The session does not exist.</exception>
    public void RequireSessionExists(string? name)
    {
        if (name is not null)
        {
            RequireSession(name);
        }
    }

    /// <summary>Returns the collection, creating it with zero counters if missing.</summary>
    public CollectionRecord GetOrAddCollection(string observerName, string observedName, DateTimeOffset now)
    {
        if (!Collections.TryGetValue((observerName, observedName), out var collection))
        {
            collection = new CollectionRecord(Name, observerName, observedName, 0, null, null, now);
            Collections.Add((observerName, observedName), collection);
        }

        return collection;
    }

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
