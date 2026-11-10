namespace Nachos.Abstractions.Domain;

/// <summary>
/// Describes the idempotency record <see cref="Stores.IMessageStore.AppendAsync(string, string, MessageAppend, CancellationToken)"/> stores with the messages.
/// </summary>
/// <param name="RequestHash">Exactly 64 lowercase hex characters (a SHA-256 digest of the request).</param>
/// <param name="SerializeResponse">
/// Builds the response body from the inserted messages. The store calls it inside the transaction, before commit.
/// It must be pure and must not re-enter any store; see <see cref="Stores.IMessageStore.AppendAsync(string, string, MessageAppend, CancellationToken)"/>.
/// </param>
/// <param name="Ttl">Added to the store's clock to produce <see cref="IdempotencyRecord.ExpiresAt"/>.</param>
public sealed record IdempotencyWrite(
    string Key,
    string RequestHash,
    int ResponseStatus,
    Func<IReadOnlyList<MessageRecord>, string> SerializeResponse,
    TimeSpan Ttl)
{
    /// <summary>
    /// The <c>CreatedAt</c> of the session incarnation <see cref="RequestHash"/> is bound to, or null when it is not bound
    /// to one. When set, the append checks it inside its transaction and throws <see cref="NotFoundException"/> if the
    /// session was deleted and recreated since, so a record is never stored under a hash bound to another incarnation.
    /// </summary>
    public DateTimeOffset? SessionCreatedAt { get; init; }
}