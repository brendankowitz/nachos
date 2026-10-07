namespace Nachos.Abstractions.Domain;

/// <summary>A stored Idempotency-Key outcome, replayed while it has not expired.</summary>
public sealed record IdempotencyRecord(
    string Key,
    string RequestHash,
    int ResponseStatus,
    string ResponseBody,
    DateTimeOffset ExpiresAt);