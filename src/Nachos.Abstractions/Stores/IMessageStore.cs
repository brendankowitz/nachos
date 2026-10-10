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
    Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
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

    /// <summary>Lists by <c>Seq</c> (descending when <see cref="PageRequest.Reverse"/>), never by <c>CreatedAt</c>.</summary>
    /// <exception cref="NotFoundException">The session does not exist.</exception>
    Task<Page<MessageRecord>> ListAsync(
        string workspaceName, string sessionName, FilterNode? filter, PageRequest page, CancellationToken ct);
}
