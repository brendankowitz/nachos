namespace Nachos.Abstractions.Domain;

/// <summary>A stored Idempotency-Key outcome, replayed while it has not expired.</summary>
/// <param name="RequestHash">Exactly 64 lowercase hex characters (a SHA-256 digest of the request).</param>
public sealed record IdempotencyRecord(
    string Key,
    string RequestHash,
    int ResponseStatus,
    string ResponseBody,
    DateTimeOffset ExpiresAt);