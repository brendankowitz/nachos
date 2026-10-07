using Nachos.Abstractions.Contracts;

namespace Nachos.Abstractions;

/// <summary>Base of every domain error. The API layer maps each subtype to an HTTP status.</summary>
public abstract class NachosException(string message) : Exception(message);

/// <summary>The addressed resource does not exist (404).</summary>
public sealed class NotFoundException(string message) : NachosException(message);

/// <summary>The request conflicts with current state (409).</summary>
public sealed class ConflictException(string message) : NachosException(message);

/// <summary>A domain rule rejected the input (422 with <c>{"detail":"..."}</c>).</summary>
public sealed class NachosValidationException(string detail) : NachosException(detail)
{
    public string Detail { get; } = detail;
}

/// <summary>The request shape is invalid (422 with the HTTPValidationError body).</summary>
public sealed class RequestValidationException(IReadOnlyList<ValidationError> errors)
    : NachosException("Request validation failed.")
{
    public IReadOnlyList<ValidationError> Errors { get; } = errors;
}

/// <summary>Authentication or scope failure (401).</summary>
public sealed class AuthException(string message) : NachosException(message);

/// <summary>An Idempotency-Key was reused with a different request (422).</summary>
public sealed class IdempotencyKeyReusedException(string message) : NachosException(message);

/// <summary>
/// Thrown by a store when an <see cref="Domain.IdempotencyWrite"/> key already has an unexpired record.
/// Callers use it to detect a lost race and replay the stored response; it is never surfaced over HTTP.
/// </summary>
public sealed class IdempotencyDuplicateException(string key)
    : NachosException($"Idempotency key '{key}' already exists.")
{
    public string Key { get; } = key;
}