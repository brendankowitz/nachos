namespace Nachos.Abstractions.Domain;

/// <summary>
/// Describes the idempotency record <see cref="Stores.IMessageStore.AppendAsync"/> stores with the messages.
/// </summary>
/// <param name="RequestHash">Exactly 64 lowercase hex characters (a SHA-256 digest of the request).</param>
/// <param name="SerializeResponse">
/// Builds the response body from the inserted messages. The store calls it inside the transaction, before commit.
/// It must be pure and must not re-enter any store; see <see cref="Stores.IMessageStore.AppendAsync"/>.
/// </param>
/// <param name="Ttl">Added to the store's clock to produce <see cref="IdempotencyRecord.ExpiresAt"/>.</param>
public sealed record IdempotencyWrite(
    string Key,
    string RequestHash,
    int ResponseStatus,
    Func<IReadOnlyList<MessageRecord>, string> SerializeResponse,
    TimeSpan Ttl);