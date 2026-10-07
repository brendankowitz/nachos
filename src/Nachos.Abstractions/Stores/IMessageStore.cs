using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

public interface IMessageStore
{
    /// <summary>
    /// Appends the messages in one transaction: upserts the sender peers and their memberships
    /// (<c>JoinedAt = now</c> when not already active), allocates contiguous per-session <c>Seq</c> values,
    /// inserts the messages, and, when <paramref name="idempotency"/> is given, inserts its record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotency contract: inside the same transaction the store first deletes an <em>expired</em> record for
    /// <c>(workspace, key)</c> (under an update or range lock, so concurrent reuse of an expired key has one
    /// winner), then inserts the new record. A key with an unexpired record makes the append throw
    /// <see cref="IdempotencyDuplicateException"/> and persist nothing.
    /// </para>
    /// <para>
    /// <see cref="IdempotencyWrite.SerializeResponse"/> is invoked inside the transaction, after the messages are
    /// inserted and before commit. If it throws, the exception propagates and neither the messages nor the
    /// record are stored.
    /// </para>
    /// </remarks>
    /// <exception cref="IdempotencyDuplicateException">The key already has an unexpired record.</exception>
    Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
        CancellationToken ct);

    /// <summary>Looks the message up by its case-sensitive public id; null when absent.</summary>
    Task<MessageRecord?> GetAsync(string workspaceName, string sessionName, string publicId, CancellationToken ct);

    /// <summary>Replaces the message's metadata.</summary>
    /// <exception cref="NotFoundException">The message does not exist.</exception>
    Task<MessageRecord> UpdateMetadataAsync(
        string workspaceName, string sessionName, string publicId, JsonObject metadata, CancellationToken ct);

    /// <summary>Lists by <c>Seq</c> (descending when <see cref="PageRequest.Reverse"/>), never by <c>CreatedAt</c>.</summary>
    Task<Page<MessageRecord>> ListAsync(
        string workspaceName, string sessionName, FilterNode? filter, PageRequest page, CancellationToken ct);
}