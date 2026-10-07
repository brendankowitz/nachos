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
    private const string SerializerModifiedStore =
        "The response serializer modified the store; it must be a pure function of the stored messages.";

    /// <remarks>
    /// <para>
    /// Atomicity: under the workspace gate the append first stages everything it will write (see
    /// <see cref="StagedAppend"/>) without touching stored state, then calls
    /// <see cref="IdempotencyWrite.SerializeResponse"/>, then confirms the staging still matches the store, and only
    /// then commits with plain insertions that cannot fail. Any exception, the caller's serializer included, leaves
    /// nothing behind and no Seq gap.
    /// </para>
    /// <para>
    /// The serializer must not call the store. It runs inside the workspace gate, which is re-entrant, so a call on the
    /// same thread would get in; if it changed anything the staging relied on, the append throws
    /// <see cref="InvalidOperationException"/> before committing.
    /// </para>
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
                var ownedMetadata = messages.Select(message => JsonCopy.Own(message.Metadata, "metadata")).ToList();
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

                    var staged = new StagedAppend(
                        workspace, session, messages, ownedMetadata, idempotency?.Key, now, state.NextOrder);
                    var result = staged.Messages.Select(JsonCopy.Out).ToList();
                    var idempotencyRecord = idempotency is null
                        ? null
                        : new IdempotencyRecord(
                            idempotency.Key,
                            idempotency.RequestHash,
                            idempotency.ResponseStatus,
                            idempotency.SerializeResponse(result),
                            now + idempotency.Ttl);

                    if (!staged.IsCurrent())
                    {
                        throw new InvalidOperationException(SerializerModifiedStore);
                    }

                    staged.Commit(idempotencyRecord);
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
                var ownedMetadata = JsonCopy.Own(metadata, "metadata");
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
}
