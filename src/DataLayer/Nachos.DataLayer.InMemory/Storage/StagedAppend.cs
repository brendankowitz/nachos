using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>
/// Everything one append will write (sender peers, memberships, messages and their <c>Seq</c> values), computed under
/// the workspace gate without touching stored state, together with the stored state it was computed from.
/// </summary>
/// <remarks>
/// Caller code runs between staging and <see cref="Commit"/> (the response serializer). <see cref="SerializeResponseGuard"/>
/// rejects its calls into the store, but a serializer that does not flow its execution context defeats the guard (out of
/// contract), and the gate is re-entrant, so it could still change the store on the same thread. <see cref="IsCurrent"/>
/// detects that, so the append can fail before anything is written instead of committing half of a plan that no longer
/// fits (for instance duplicate <c>Seq</c> values).
/// </remarks>
internal sealed class StagedAppend
{
    private readonly WorkspaceEntry _workspace;
    private readonly SessionEntry _session;
    private readonly long _baseSeq;
    private readonly string? _idempotencyKey;
    private readonly IdempotencyRecord? _baseIdempotency;
    private readonly Dictionary<string, Membership?> _baseMemberships = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeerEntry> _newPeers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Membership> _memberships = new(StringComparer.Ordinal);
    private readonly List<MessageRecord> _messages;

    /// <summary>Stages an append. The caller holds the workspace gate.</summary>
    public StagedAppend(
        WorkspaceEntry workspace,
        SessionEntry session,
        IReadOnlyList<NewMessage> messages,
        IReadOnlyList<JsonObject> ownedMetadata,
        string? idempotencyKey,
        DateTimeOffset now,
        Func<long> nextOrder)
    {
        _workspace = workspace;
        _session = session;
        _baseSeq = session.LastSeq;
        _idempotencyKey = idempotencyKey;
        _baseIdempotency = idempotencyKey is null ? null : workspace.Idempotency.GetValueOrDefault(idempotencyKey);

        foreach (var sender in messages.Select(message => message.PeerName).Distinct(StringComparer.Ordinal))
        {
            if (!workspace.Peers.ContainsKey(sender))
            {
                _newPeers.Add(sender, PeerEntry.CreateDefault(workspace.Name, sender, now, nextOrder()));
            }

            // A former member is reactivated in place, keeping its config.
            var membership = session.Members.GetValueOrDefault(sender);
            _baseMemberships.Add(sender, membership);
            if (membership is not { IsActive: true })
            {
                _memberships.Add(
                    sender,
                    membership is null
                        ? new Membership(new SessionPeerConfig(), now, LeftAt: null)
                        : membership with { JoinedAt = now, LeftAt = null });
            }
        }

        var batchIds = new HashSet<string>(StringComparer.Ordinal);
        _messages = [.. messages.Select((message, i) => new MessageRecord(
            NewPublicId(session, batchIds),
            workspace.Name,
            session.Record.Name,
            message.PeerName,
            _baseSeq + i + 1,
            message.Content,
            message.TokenCount,
            ownedMetadata[i],
            message.CreatedAt ?? now))];
    }

    /// <summary>The staged messages. Store-owned: clone before handing them out.</summary>
    public IReadOnlyList<MessageRecord> Messages => _messages;

    /// <summary>
    /// True when nothing the append depends on has changed. Fails if any state the staging relied on, or any
    /// membership/idempotency record, was replaced. Rows are immutable records replaced on every change, so reference
    /// equality detects any change; the session's <c>LastSeq</c> and the staged peers are checked directly.
    /// </summary>
    public bool IsCurrent() =>
        _session.LastSeq == _baseSeq
        && _newPeers.Keys.All(name => !_workspace.Peers.ContainsKey(name))
        && _baseMemberships.All(pair => ReferenceEquals(_session.Members.GetValueOrDefault(pair.Key), pair.Value))
        && (_idempotencyKey is null
            || ReferenceEquals(_workspace.Idempotency.GetValueOrDefault(_idempotencyKey), _baseIdempotency));

    /// <summary>
    /// Writes the staged rows and the optional idempotency record (replacing an expired one for the same key). Only
    /// valid while <see cref="IsCurrent"/>; then no step can fail, so the commit is all-or-nothing.
    /// </summary>
    public void Commit(IdempotencyRecord? idempotency)
    {
        foreach (var (name, peer) in _newPeers)
        {
            _workspace.Peers.Add(name, peer);
        }

        foreach (var (name, membership) in _memberships)
        {
            _session.Members[name] = membership;
        }

        foreach (var message in _messages)
        {
            _session.AppendMessage(message);
        }

        if (idempotency is not null)
        {
            _workspace.Idempotency[idempotency.Key] = idempotency;
        }
    }

    /// <summary>
    /// A public id unused in the session and in this batch. Collisions are astronomically unlikely, but the commit must
    /// not be able to fail, so they are ruled out while staging.
    /// </summary>
    private static string NewPublicId(SessionEntry session, HashSet<string> batch)
    {
        string id;
        do
        {
            id = PublicId.New();
        }
        while (session.ContainsMessage(id) || !batch.Add(id));

        return id;
    }
}
