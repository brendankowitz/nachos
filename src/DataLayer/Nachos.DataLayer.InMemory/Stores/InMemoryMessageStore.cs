using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
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
    /// <para>
    /// The chunks and work items are built while staging, from the staged messages, peers and memberships; the chunks are
    /// stored with the staged rows and the work is enqueued under the queue gate right after, still inside the workspace
    /// gate: whatever stops the append (the serializer, an idempotency duplicate) stops its chunks and work too, and no
    /// claimer sees work before its messages.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName, string sessionName, MessageAppend append, CancellationToken ct) =>
        state.Run<IReadOnlyList<MessageRecord>>(
            () =>
            {
                ArgumentNullException.ThrowIfNull(append);
                var messages = append.Messages;
                RequireOnePerMessage(append.Derive?.Count, messages.Count, nameof(append.Derive));
                RequireOnePerMessage(append.Chunks?.Count, messages.Count, nameof(append.Chunks));
                if (append.Chunks?.Any(chunks => chunks.Count > MessageChunk.MaxChunkIndex + 1 || chunks.Any(chunk => chunk is null)) == true)
                {
                    throw new ArgumentException(
                        $"A message may have at most {MessageChunk.MaxChunkIndex + 1} chunks, none of them null.", nameof(append));
                }

                var ownedMetadata = messages.Select(message => JsonCopy.Own(message.Metadata, "metadata")).ToList();
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var session = workspace.RequireWritableSession(sessionName);
                    var now = state.Clock.GetUtcNow();
                    var idempotency = append.Idempotency;

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
                    var chunks = append.Chunks is null ? [] : Chunks(staged, append.Chunks, now);
                    var pendingWork = append.Derive is null ? [] : RepresentationWork(workspaceName, staged, append.Derive, now);
                    if (append.Summary is { } summary)
                    {
                        pendingWork.AddRange(SummaryWork(workspaceName, staged, summary, now));
                    }

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
                    foreach (var chunk in chunks)
                    {
                        session.Chunks[(chunk.MessageId, chunk.ChunkIndex)] = chunk;
                    }

                    if (pendingWork.Count > 0)
                    {
                        lock (state.Queue.Gate)
                        {
                            foreach (var work in pendingWork)
                            {
                                state.Queue.Enqueue(work);
                            }
                        }
                    }

                    return result;
                }
            },
            ct);

    /// <exception cref="ArgumentException">A list is given and does not have one entry per message.</exception>
    private static void RequireOnePerMessage(int? count, int messageCount, string name)
    {
        if (count is { } given && given != messageCount)
        {
            throw new ArgumentException($"Expected one {name} entry per message ({messageCount}), got {given}.", name);
        }
    }

    /// <summary>The staged messages' chunks, pending, in message then chunk order.</summary>
    private List<StoredChunk> Chunks(StagedAppend staged, IReadOnlyList<IReadOnlyList<string>> chunks, DateTimeOffset now) =>
    [
        .. staged.Messages.Zip(chunks).SelectMany(pair => pair.Second.Select((content, index) =>
            StoredChunk.Pending(pair.First.PublicId, index, content, now, state.NextOrder()))),
    ];

    /// <summary>One <see cref="WorkTaskType.Representation"/> item per derived message whose sender has an observer.</summary>
    private static List<Func<long, QueuedItem>> RepresentationWork(
        string workspaceName, StagedAppend staged, IReadOnlyList<DeriveIntent> derive, DateTimeOffset now)
    {
        var work = new List<Func<long, QueuedItem>>();
        foreach (var (message, intent) in staged.Messages.Zip(derive))
        {
            var observers = intent.ShouldDerive ? staged.ObserversOf(message) : [];
            if (observers.Count == 0)
            {
                continue;
            }

            var key = WorkUnitKeys.Representation(workspaceName, message.SessionName, message.PeerName);
            work.Add(id => new QueuedItem(
                id,
                workspaceName,
                WorkTaskType.Representation,
                key,
                RepresentationPayload.Create(observers, intent.ReasoningEnabled, intent.CustomInstructions),
                message.TokenCount,
                message.SessionName,
                message.PublicId,
                message.PeerName,
                observers,
                now));
        }

        return work;
    }

    /// <summary>One <see cref="WorkTaskType.Summary"/> item per kind whose threshold the staged <c>Seq</c> range crosses.</summary>
    private static IEnumerable<Func<long, QueuedItem>> SummaryWork(
        string workspaceName, StagedAppend staged, SummaryIntent summary, DateTimeOffset now)
    {
        if (staged.Messages.Count == 0)
        {
            yield break;
        }

        var first = staged.Messages[0].Seq;
        foreach (var kind in Enum.GetValues<SummaryKind>())
        {
            if (summary.DueThrough(kind, first, staged.Messages[^1].Seq) is not { } throughSeq)
            {
                continue;
            }

            var message = staged.Messages[(int)(throughSeq - first)];
            yield return id => new QueuedItem(
                id,
                workspaceName,
                WorkTaskType.Summary,
                WorkUnitKeys.Summary(workspaceName, message.SessionName),
                SummaryPayload.Create(kind, throughSeq),
                tokenCount: 0,
                message.SessionName,
                message.PublicId,
                senderName: null,
                observers: [],
                now);
        }
    }

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
                    var session = workspace.RequireWritableSession(sessionName);
                    var message = session.FindMessage(publicId)
                        ?? throw new NotFoundException($"Message '{publicId}' not found in session '{sessionName}'.");
                    var updated = message with { Metadata = ownedMetadata };
                    session.ReplaceMessage(updated);
                    return JsonCopy.Out(updated);
                }
            },
            ct);

    public Task<IReadOnlyList<MessageRecord>> ListRangeAsync(
        string workspaceName, string sessionName, long fromSeq, long toSeq, CancellationToken ct) =>
        state.Run<IReadOnlyList<MessageRecord>>(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return [.. workspace.RequireSession(sessionName).Messages
                        .Where(message => message.Seq >= fromSeq && message.Seq <= toSeq)
                        .Select(JsonCopy.Out)];
                }
            },
            ct);

    public Task<Page<MessageRecord>> ListAsync(
        string workspaceName, string sessionName, FilterNode? filter, PageRequest page, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                var prepared = InMemoryFilterEvaluator.Prepare(filter);
                lock (workspace.Gate)
                {
                    var rows = workspace.RequireSession(sessionName).Messages
                        .Where(message => InMemoryFilterEvaluator.Matches(prepared, message));
                    return Paging.ToPage(rows, page, JsonCopy.Out);
                }
            },
            ct);
}
