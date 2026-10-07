using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

internal sealed class InMemoryMessageStore(InMemoryState state) : IMessageStore
{
    /// <remarks>
    /// Atomicity: under the workspace gate the append first <i>stages</i> everything it will write (sender peers,
    /// memberships, messages with their Seq values, the idempotency record) without touching stored state, then calls
    /// <see cref="IdempotencyWrite.SerializeResponse"/>, and only then commits with plain insertions that cannot fail.
    /// So any exception, the caller's serializer included, leaves nothing behind and no Seq gap.
    /// </remarks>
    public Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
        CancellationToken ct) =>
        StoreTask.Run<IReadOnlyList<MessageRecord>>(
            () =>
            {
                var ownedMetadata = messages.Select(message => JsonCopy.Own(message.Metadata)).ToList();
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var session = workspace.RequireSession(sessionName);
                    var now = state.Clock.GetUtcNow();

                    // An expired record is reclaimed by the overwrite at commit; an unexpired one wins.
                    if (idempotency is not null
                        && workspace.Idempotency.TryGetValue(idempotency.Key, out var existing)
                        && existing.ExpiresAt > now)
                    {
                        throw new IdempotencyDuplicateException(idempotency.Key);
                    }

                    // Stage.
                    var newPeers = new Dictionary<string, PeerEntry>(StringComparer.Ordinal);
                    var memberships = new Dictionary<string, Membership>(StringComparer.Ordinal);
                    foreach (var sender in messages.Select(message => message.PeerName).Distinct(StringComparer.Ordinal))
                    {
                        if (!workspace.Peers.ContainsKey(sender))
                        {
                            newPeers.Add(sender, PeerEntry.CreateDefault(workspaceName, sender, now, state.NextOrder()));
                        }

                        // A former member is reactivated in place, keeping its config.
                        var membership = session.Members.GetValueOrDefault(sender);
                        if (membership is not { IsActive: true })
                        {
                            memberships.Add(
                                sender,
                                membership is null
                                    ? new Membership(new SessionPeerConfig(), now, LeftAt: null)
                                    : membership with { JoinedAt = now, LeftAt = null });
                        }
                    }

                    var publicIds = new HashSet<string>(StringComparer.Ordinal);
                    var records = new List<MessageRecord>(messages.Count);
                    for (var i = 0; i < messages.Count; i++)
                    {
                        var message = messages[i];
                        records.Add(new MessageRecord(
                            NewPublicId(session, publicIds),
                            workspaceName,
                            sessionName,
                            message.PeerName,
                            session.LastSeq + i + 1,
                            message.Content,
                            message.TokenCount,
                            ownedMetadata[i],
                            message.CreatedAt ?? now));
                    }

                    var result = records.Select(JsonCopy.Out).ToList();
                    var idempotencyRecord = idempotency is null
                        ? null
                        : new IdempotencyRecord(
                            idempotency.Key,
                            idempotency.RequestHash,
                            idempotency.ResponseStatus,
                            idempotency.SerializeResponse(result),
                            now + idempotency.Ttl);

                    // Commit.
                    foreach (var (name, peer) in newPeers)
                    {
                        workspace.Peers.Add(name, peer);
                    }

                    foreach (var (name, membership) in memberships)
                    {
                        session.Members[name] = membership;
                    }

                    foreach (var record in records)
                    {
                        session.AppendMessage(record);
                    }

                    if (idempotencyRecord is not null)
                    {
                        workspace.Idempotency[idempotencyRecord.Key] = idempotencyRecord;
                    }

                    return result;
                }
            },
            ct);

    public Task<MessageRecord?> GetAsync(
        string workspaceName, string sessionName, string publicId, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return workspace.RequireSession(sessionName).FindMessage(publicId) is { } message
                        ? JsonCopy.Out(message)
                        : null;
                }
            },
            ct);

    public Task<MessageRecord> UpdateMetadataAsync(
        string workspaceName, string sessionName, string publicId, JsonObject metadata, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var ownedMetadata = JsonCopy.Own(metadata);
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var session = workspace.RequireSession(sessionName);
                    var message = session.FindMessage(publicId)
                        ?? throw new NotFoundException($"Message '{publicId}' not found in session '{sessionName}'.");
                    var updated = message with { Metadata = ownedMetadata };
                    session.ReplaceMessage(updated);
                    return JsonCopy.Out(updated);
                }
            },
            ct);

    public Task<Page<MessageRecord>> ListAsync(
        string workspaceName, string sessionName, FilterNode? filter, PageRequest page, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var rows = workspace.RequireSession(sessionName).Messages
                        .Where(message => InMemoryFilterEvaluator.Matches(filter, message));
                    return Paging.ToPage(rows, page, JsonCopy.Out);
                }
            },
            ct);

    /// <summary>
    /// A public id unused in the session and in this batch. Collisions are astronomically unlikely, but the commit
    /// phase must not be able to fail, so they are ruled out while staging.
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
