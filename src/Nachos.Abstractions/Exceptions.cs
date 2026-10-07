using Nachos.Abstractions.Contracts;

namespace Nachos.Abstractions;

/// <summary>Base of every domain error. The API layer maps each subtype to an HTTP status.</summary>
public abstract class NachosException : Exception
{
    protected NachosException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The addressed resource does not exist (404).</summary>
public sealed class NotFoundException : NachosException
{
    public NotFoundException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The request conflicts with current state (409).</summary>
public sealed class ConflictException : NachosException
{
    public ConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>A domain rule rejected the input (422 with <c>{"detail":"..."}</c>).</summary>
public sealed class NachosValidationException : NachosException
{
    public NachosValidationException(string detail, Exception? innerException = null)
        : base(detail, innerException)
    {
        Detail = detail;
    }

    public string Detail { get; }
}

/// <summary>The request shape is invalid (422 with the HTTPValidationError body).</summary>
public sealed class RequestValidationException : NachosException
{
    public RequestValidationException(IReadOnlyList<ValidationError> errors, Exception? innerException = null)
        : base("Request validation failed.", innerException)
    {
        Errors = errors;
    }

    public IReadOnlyList<ValidationError> Errors { get; }
}

/// <summary>Authentication or scope failure (401).</summary>
public sealed class AuthException : NachosException
{
    public AuthException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>An Idempotency-Key was reused with a different request (422).</summary>
public sealed class IdempotencyKeyReusedException : NachosException
{
    public IdempotencyKeyReusedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown by a store when an <see cref="Domain.IdempotencyWrite"/> key already has an unexpired record.
/// Callers use it to detect a lost race and replay the stored response; it is never surfaced over HTTP.
/// </summary>
public sealed class IdempotencyDuplicateException : NachosException
{
    public IdempotencyDuplicateException(string key, Exception? innerException = null)
        : base($"Idempotency key '{key}' already exists.", innerException)
    {
        Key = key;
    }

    public string Key { get; }
}