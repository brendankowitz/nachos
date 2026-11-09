namespace Nachos.Abstractions.Domain;

/// <summary>
/// Everything one <see cref="Stores.IMessageStore.AppendAsync(string, string, MessageAppend, CancellationToken)"/>
/// writes in its transaction.
/// </summary>
/// <param name="Messages">The messages to append, in order.</param>
/// <param name="Idempotency">The idempotency record to store with them, or null.</param>
/// <param name="Derive">One derivation intent per message, in order; null enqueues no derivation work.</param>
/// <param name="Chunks">
/// One entry per message, in order: the message's embedding chunks, possibly none. Null stores no chunks.
/// </param>
/// <param name="Summary">The session's summary configuration; null enqueues no summary work.</param>
public sealed record MessageAppend(
    IReadOnlyList<NewMessage> Messages,
    IdempotencyWrite? Idempotency = null,
    IReadOnlyList<DeriveIntent>? Derive = null,
    IReadOnlyList<IReadOnlyList<string>>? Chunks = null,
    SummaryIntent? Summary = null);
