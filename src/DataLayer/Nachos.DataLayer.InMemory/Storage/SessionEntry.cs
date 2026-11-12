using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>A stored session with its memberships, messages, chunks and summaries. Guarded by its workspace's gate.</summary>
/// <remarks>
/// Invariants: a message's <c>Seq</c> is its 1-based position in <see cref="Messages"/> (so Seq values are contiguous
/// from 1; the deletion purge removes messages from the end to keep it so), every membership names a peer that exists in
/// the workspace, every chunk names a message of the session, and <see cref="DeletionJobId"/> is set exactly while the
/// session is <see cref="LifecycleState.Deleting"/>.
/// </remarks>
internal sealed class SessionEntry(SessionRecord record, long order)
{
    private readonly List<MessageRecord> _messages = [];
    private readonly List<long> _messageOrders = [];
    private readonly Dictionary<string, int> _positionByPublicId = new(StringComparer.Ordinal);

    public long Order { get; } = order;

    public SessionRecord Record { get; set; } = record;

    /// <summary>The work item id of the session's deletion; set exactly while it is being deleted.</summary>
    public long? DeletionJobId { get; private set; }

    public bool IsDeleting => DeletionJobId is not null;

    public Dictionary<string, Membership> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>The messages' embedding chunks by (message public id, chunk index).</summary>
    public Dictionary<(string MessageId, int ChunkIndex), StoredChunk> Chunks { get; } = [];

    /// <summary>The public ids of the session's conclusions its deletion job has deleted: the job's checkpoint for D7.</summary>
    public HashSet<string> PurgedConclusionIds { get; } = new(StringComparer.Ordinal);

    /// <summary>The current summary of each kind.</summary>
    public Dictionary<SummaryKind, SummaryRecord> Summaries { get; } = [];

    /// <exception cref="ConflictException">The session is being deleted.</exception>
    public void RequireNotDeleting()
    {
        if (IsDeleting)
        {
            throw new ConflictException("session is being deleted");
        }
    }

    public void MarkDeleting(long deletionJobId)
    {
        DeletionJobId = deletionJobId;
        Record = Record with { State = LifecycleState.Deleting };
    }

    /// <summary>The messages in <c>Seq</c> order.</summary>
    public IReadOnlyList<MessageRecord> Messages => _messages;

    public long LastSeq => _messages.Count;

    public IEnumerable<string> ActiveMemberNames => Members.Where(m => m.Value.IsActive).Select(m => m.Key);

    public bool Matches(PreparedFilter? filter) =>
        filter is null || InMemoryFilterEvaluator.Matches(filter, Record, [.. ActiveMemberNames]);

    public bool IsActiveMember(string peerName) => Members.GetValueOrDefault(peerName)?.IsActive == true;

    /// <summary>
    /// Makes the peer an active member with <paramref name="config"/>: an active member keeps its <c>JoinedAt</c>,
    /// anyone else (new or former member) joins now. Reactivation reuses the existing row.
    /// </summary>
    public void Activate(string peerName, SessionPeerConfig config, DateTimeOffset now) =>
        Members[peerName] = Members.TryGetValue(peerName, out var existing) && existing.IsActive
            ? existing with { Config = config }
            : new Membership(config, now, LeftAt: null);

    /// <summary>Marks an active member as left; anyone else is a no-op.</summary>
    public void Leave(string peerName, DateTimeOffset now)
    {
        if (Members.TryGetValue(peerName, out var existing) && existing.IsActive)
        {
            Members[peerName] = existing with { LeftAt = now };
        }
    }

    public bool ContainsMessage(string publicId) => _positionByPublicId.ContainsKey(publicId);

    public MessageRecord? FindMessage(string publicId) =>
        _positionByPublicId.TryGetValue(publicId, out var position) ? _messages[position] : null;

    /// <summary>The messages in <c>Seq</c> order, each with its store-wide insertion number.</summary>
    public IEnumerable<(MessageRecord Message, long Order)> MessagesWithOrder => _messages.Zip(_messageOrders);

    /// <summary>Appends a message whose <c>Seq</c> is <see cref="LastSeq"/> + 1 and whose public id is unused.</summary>
    /// <param name="order">The message's store-wide insertion number (<see cref="InMemoryState.NextOrder"/>).</param>
    public void AppendMessage(MessageRecord message, long order)
    {
        if (message.Seq != LastSeq + 1)
        {
            throw new InvalidOperationException($"Seq {message.Seq} does not follow {LastSeq} in session '{Record.Name}'.");
        }

        _positionByPublicId.Add(message.PublicId, _messages.Count);
        _messages.Add(message);
        _messageOrders.Add(order);
    }

    /// <summary>Replaces the stored message that has the same public id.</summary>
    public void ReplaceMessage(MessageRecord message) => _messages[_positionByPublicId[message.PublicId]] = message;

    /// <summary>Removes the newest message, so the remaining <c>Seq</c> values stay positions.</summary>
    public void RemoveLastMessage()
    {
        _positionByPublicId.Remove(_messages[^1].PublicId);
        _messages.RemoveAt(_messages.Count - 1);
        _messageOrders.RemoveAt(_messageOrders.Count - 1);
    }
}
