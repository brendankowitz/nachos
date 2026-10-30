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
    /// and, when <paramref name="idempotency"/> is given, inserts its record.
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
    /// </remarks>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    /// <exception cref="IdempotencyDuplicateException">The key already has an unexpired record.</exception>
    /// <remarks>Enqueues no work; the same as the overload with a null <c>derive</c>.</remarks>
    Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
        CancellationToken ct);

    /// <summary>
    /// Appends the messages exactly as the overload without <paramref name="derive"/> does and, in the same transaction,
    /// enqueues their derivation work.
    /// </summary>
    /// <remarks>
    /// For each message whose intent has <see cref="DeriveIntent.ShouldDerive"/>, the store resolves the observers with
    /// <see cref="Observation.ObserverRule"/> at the message's <c>CreatedAt</c>, over the memberships as the append
    /// leaves them, and, when there is at least one, enqueues one <see cref="WorkTaskType.Representation"/> item:
    /// key <see cref="Work.WorkUnitKeys.Representation"/> (workspace, session, sender), payload
    /// <see cref="Work.RepresentationPayload"/> (the observers, plus the intent's <see cref="DeriveIntent.ReasoningEnabled"/>
    /// and <see cref="DeriveIntent.CustomInstructions"/>), the message's <c>TokenCount</c> and public id, enqueued now. Whenever
    /// the append stores nothing (a serializer throw, an idempotency duplicate), it enqueues nothing.
    /// </remarks>
    /// <param name="derive">One intent per message, in order; null enqueues nothing.</param>
    /// <exception cref="ArgumentException"><paramref name="derive"/> does not have one intent per message.</exception>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    /// <exception cref="IdempotencyDuplicateException">The key already has an unexpired record.</exception>
    Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
        IReadOnlyList<DeriveIntent>? derive,
        CancellationToken ct);

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
