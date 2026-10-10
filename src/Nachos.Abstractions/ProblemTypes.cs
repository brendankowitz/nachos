namespace Nachos.Abstractions;

/// <summary>RFC 9457 problem <c>type</c> URIs that clients use to distinguish errors that share an HTTP status.</summary>
public static class ProblemTypes
{
    /// <summary>
    /// Problem type for <see cref="IdempotencyKeyReusedException"/>: an <c>Idempotency-Key</c> reused with a different request.
    /// It is sent with status 422, the same status as <see cref="NachosValidationException"/>, so clients must use this type to tell the two apart.
    /// </summary>
    public const string IdempotencyKeyReused = "urn:nachos:problem:idempotency-key-reused";
}
