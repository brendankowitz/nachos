using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// Message storage. See <see cref="IMemoryStore"/> for the rules shared by all stores. Every operation throws
/// <see cref="NotFoundException"/> when the session (or its workspace) does not exist.
/// </summary>
public interface IMessageStore
{
    /// <summary>
    /// Appends the messages in one transaction: upserts the sender peers and their memberships
    /// (<c>JoinedAt = now</c> when not already active; a sender who previously left is reactivated in place with its
    /// previous membership configuration kept, unlike <see cref="ISessionStore.AddPeersAsync"/> which sets config), allocates contiguous per-session <c>Seq</c> values,
    /// inserts the messages (null metadata becomes <c>{}</c>; a null <see cref="NewMessage.CreatedAt"/> becomes now),
    /// and, when <see cref="MessageAppend.Idempotency"/> is given, inserts its record; then stores the chunks and
    /// enqueues the work described below.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotency contract: inside the same transaction the store first deletes an <em>expired</em> record for
    /// <c>(workspace, key)</c> (a record is expired when <c>ExpiresAt &lt;= now</c>), under an update or range lock so
    /// concurrent reuse of an expired key, or concurrent use of one fresh key, has exactly one winner; then it
    /// inserts the new record. A key with an unexpired record makes the append throw
    /// <see cref="IdempotencyDuplicateException"/> and persist nothing.
    /// </para>
    /// <para>
    /// <see cref="IdempotencyWrite.SerializeResponse"/> is invoked inside the transaction, after the messages are
    /// inserted and before commit. If it throws, the exception propagates and nothing is stored: no messages, no
    /// idempotency record, no sender peers or memberships created by the attempt, and no <c>Seq</c> gap.
    /// </para>
    /// <para>
    /// <see cref="IdempotencyWrite.SerializeResponse"/> must be a pure function of the <see cref="MessageRecord"/>s it
    /// is given. It runs after the rows are staged and before commit, so it sees no transactional read guarantee beyond
    /// those records, and it must not call back into any store. A provider must fail fast and deterministically, by
    /// throwing <see cref="InvalidOperationException"/>, if the store is re-entered from within it.
    /// </para>
    /// <para>
    /// <b>Derivation work</b> (<see cref="MessageAppend.Derive"/>): for each message whose intent has
    /// <see cref="DeriveIntent.ShouldDerive"/>, the store resolves the observers with
    /// <see cref="Observation.ObserverRule"/> at the message's <c>CreatedAt</c>, over the memberships as the append
    /// leaves them, and, when there is at least one, enqueues one <see cref="WorkTaskType.Representation"/> item:
    /// key <see cref="Work.WorkUnitKeys.Representation"/> (workspace, session, sender), payload
    /// <see cref="Work.RepresentationPayload"/> (the observers, plus the intent's <see cref="DeriveIntent.ReasoningEnabled"/>
    /// and <see cref="DeriveIntent.CustomInstructions"/>), the message's <c>TokenCount</c> and public id, enqueued now.
    /// </para>
    /// <para>
    /// <b>Chunks</b> (<see cref="MessageAppend.Chunks"/>): each message's chunks are stored, in order from chunk index 0,
    /// as pending embeddings (<see cref="PendingEmbeddingKind.MessageChunk"/>) for <see cref="IEmbeddingStore"/>.
    /// </para>
    /// <para>
    /// <b>Summary work</b> (<see cref="MessageAppend.Summary"/>): for each <see cref="SummaryKind"/> whose
    /// <see cref="SummaryIntent.DueThrough"/> the batch's <c>Seq</c> range crosses, one <see cref="WorkTaskType.Summary"/>
    /// item: key <see cref="Work.WorkUnitKeys.Summary"/>, payload <see cref="Work.SummaryPayload"/>, token count 0, the
    /// public id of the message at <c>through_seq</c>, enqueued now, after the derivation items.
    /// </para>
    /// <para>Whenever the append stores nothing (a serializer throw, an idempotency duplicate), it stores no chunks and enqueues nothing.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <see cref="MessageAppend.Derive"/> or <see cref="MessageAppend.Chunks"/> does not have one entry per message, or a
    /// message has more than <see cref="MessageChunk.MaxChunkIndex"/> + 1 chunks.
    /// </exception>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    /// <exception cref="ConflictException">The workspace or the session is being deleted.</exception>
    /// <exception cref="IdempotencyDuplicateException">The key already has an unexpired record.</exception>
    Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName, string sessionName, MessageAppend append, CancellationToken ct);

    /// <summary>
    /// <see cref="AppendAsync(string, string, MessageAppend, CancellationToken)"/> with no derivation, chunks or summary
    /// work.
    /// </summary>
    Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
        CancellationToken ct) =>
        AppendAsync(workspaceName, sessionName, new MessageAppend(messages, idempotency), ct);

    /// <summary>
    /// <see cref="AppendAsync(string, string, MessageAppend, CancellationToken)"/> with derivation work only.
    /// </summary>
    /// <param name="derive">One intent per message, in order; null enqueues nothing.</param>
    Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
        IReadOnlyList<DeriveIntent>? derive,
        CancellationToken ct) =>
        AppendAsync(workspaceName, sessionName, new MessageAppend(messages, idempotency, derive), ct);

    /// <summary>
    /// Looks the message up by its case-sensitive public id. Returns null only when the <i>message</i> is missing
    /// from an existing session.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<MessageRecord?> GetAsync(string workspaceName, string sessionName, string publicId, CancellationToken ct);

    /// <summary>
    /// <b>Replaces</b> the message's metadata. Content, <c>Seq</c>, and <c>CreatedAt</c> are untouched.
    /// </summary>
    /// <exception cref="NotFoundException">The session or the message does not exist.</exception>
    /// <exception cref="ConflictException">The workspace or the session is being deleted.</exception>
    Task<MessageRecord> UpdateMetadataAsync(
        string workspaceName, string sessionName, string publicId, JsonObject metadata, CancellationToken ct);

    /// <summary>
    /// The session's messages whose <c>Seq</c> is between <paramref name="fromSeq"/> and <paramref name="toSeq"/>
    /// inclusive, in <c>Seq</c> order; empty when the range is empty or holds no messages.
    /// </summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<IReadOnlyList<MessageRecord>> ListRangeAsync(
        string workspaceName, string sessionName, long fromSeq, long toSeq, CancellationToken ct);

    /// <summary>Lists by <c>Seq</c> (descending when <see cref="PageRequest.Reverse"/>), never by <c>CreatedAt</c>.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Page<MessageRecord>> ListAsync(
        string workspaceName, string sessionName, FilterNode? filter, PageRequest page, CancellationToken ct);
}
