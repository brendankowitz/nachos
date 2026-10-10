using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>A stored session with its memberships and messages. Guarded by its workspace's gate.</summary>
/// <remarks>
/// Invariants: a message's <c>Seq</c> is its 1-based position in <see cref="Messages"/> (so Seq values are contiguous
/// from 1), and every membership names a peer that exists in the workspace.
/// </remarks>
internal sealed class SessionEntry(SessionRecord record, long order)
{
    private readonly List<MessageRecord> _messages = [];
    private readonly Dictionary<string, int> _positionByPublicId = new(StringComparer.Ordinal);

    public long Order { get; } = order;

    public SessionRecord Record { get; set; } = record;

    public Dictionary<string, Membership> Members { get; } = new(StringComparer.Ordinal);

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

    /// <summary>Appends a message whose <c>Seq</c> is <see cref="LastSeq"/> + 1 and whose public id is unused.</summary>
    public void AppendMessage(MessageRecord message)
    {
        if (message.Seq != LastSeq + 1)
        {
            throw new InvalidOperationException($"Seq {message.Seq} does not follow {LastSeq} in session '{Record.Name}'.");
        }

        _positionByPublicId.Add(message.PublicId, _messages.Count);
        _messages.Add(message);
    }

    /// <summary>Replaces the stored message that has the same public id.</summary>
    public void ReplaceMessage(MessageRecord message) => _messages[_positionByPublicId[message.PublicId]] = message;
}
