namespace Nachos.Client;

/// <summary>Keys of <see cref="Exception.Data"/> entries that the Nachos client sets on the exceptions it raises.</summary>
public static class NachosExceptionData
{
    /// <summary>
    /// The delay a failed response asked for in its <c>Retry-After</c> header, as a <see cref="TimeSpan"/> (zero for an
    /// HTTP-date already in the past). Present only when the response carried a parseable <c>Retry-After</c>
    /// (delta-seconds or HTTP-date); an absent or unparseable header sets no entry. Spec §16: a delay longer than
    /// <see cref="RetryHandler.MaxRetryAfter"/> is not waited out but surfaced with this entry.
    /// </summary>
    /// <remarks>
    /// The same exception's message then ends with <c>" Retry-After: {N}s."</c>, the delay rounded up to whole seconds,
    /// except for <see cref="Nachos.Abstractions.RequestValidationException"/>, whose message is fixed.
    /// </remarks>
    public const string RetryAfter = "Nachos.RetryAfter";

    /// <summary>Reads the <see cref="RetryAfter"/> entry of <paramref name="exception"/>.</summary>
    /// <returns>True when the entry is present and is a <see cref="TimeSpan"/>; false otherwise.</returns>
    public static bool TryGetRetryAfter(Exception exception, out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Data[RetryAfter] is TimeSpan value)
        {
            delay = value;
            return true;
        }

        delay = default;
        return false;
    }
}
