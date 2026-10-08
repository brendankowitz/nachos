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
    /// <para>
    /// Atomicity: under the workspace gate the append first stages everything it will write (see
    /// <see cref="StagedAppend"/>) without touching stored state, then calls
    /// <see cref="IdempotencyWrite.SerializeResponse"/>, then confirms the staging still matches the store, and only
    /// then commits with plain insertions that cannot fail. Any exception, the caller's serializer included, leaves
    /// nothing behind and no Seq gap.
    /// </para>
    /// <para>
    /// The serializer must be a pure function of the staged records it receives: it must not call the store and gets
    /// no transactional read. It receives its own deep copies, so mutating them changes neither the result of the
    /// append nor the stored data. While it runs, every entry point of this store rejects calls from its execution context
    /// with <see cref="InvalidOperationException"/> before taking any lock (see <see cref="SerializeResponseGuard"/>),
    /// and if any call was attempted the append throws <see cref="InvalidOperationException"/> after the serializer
    /// returns, even if the serializer swallowed the rejection. A serializer that lets the rejection propagate fails
    /// the append with that same exception. Either way nothing is stored.
    /// </para>
    /// <para>
    /// Edge cases of that contract: (a) a serializer that re-enters, swallows the rejection and then throws a
    /// different exception fails the append with that other exception; (b) work the serializer starts that re-enters
    /// while the serializer is still running is always rejected, but whether the append fails depends on whether that
    /// attempt lands before the latch is read, so such work is out of contract; (c) a serializer that blocks on
    /// <c>Task.Run(...).Wait()</c> and lets the rejection propagate fails the append with its own
    /// <see cref="AggregateException"/>, not with the rejection itself.
    /// </para>
    /// <para>
    /// The staleness check before commit is defence in depth for a serializer that defeats the guard by not flowing
    /// its execution context (out of contract): the gate is re-entrant, so such a call on the same thread gets in, and
    /// if it changed anything the staging relied on, the append throws the same exception instead of committing.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
        CancellationToken ct) =>
        state.Run<IReadOnlyList<MessageRecord>>(
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
                            state.SerializeResponseGuard.Invoke(
                                idempotency.SerializeResponse, staged.Messages.Select(JsonCopy.Out).ToList()),
                            now + idempotency.Ttl);

                    if (!staged.IsCurrent())
                    {
                        throw new InvalidOperationException(SerializeResponseGuard.ReentryMessage);
                    }

                    staged.Commit(idempotencyRecord);
                    return result;
                }
            },
            ct);

    public Task<MessageRecord?> GetAsync(
        string workspaceName, string sessionName, string publicId, CancellationToken ct) =>
        state.Run(
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
        state.Run(
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
        state.Run(
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
