using System.Globalization;

namespace Nachos.Client;

/// <summary>
/// The one reading of a response's <c>Retry-After</c>, shared by the retry wait (<see cref="RetryHandler"/>) and the
/// delay surfaced on exceptions (<see cref="NachosExceptionData.RetryAfter"/>), so the two never disagree.
/// </summary>
internal static class RetryAfterHeader
{
    /// <summary>
    /// The delay <paramref name="response"/> asked for: delta-seconds as given, or an HTTP-date less the current time
    /// (zero when the date is past). Null when the header is absent or unparseable.
    /// </summary>
    public static TimeSpan? Delay(HttpResponseMessage response, TimeProvider clock)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - clock.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>The message suffix for <paramref name="delay"/>: <c>" Retry-After: {N}s."</c>, rounded up to whole seconds.</summary>
    public static string Suffix(TimeSpan delay) =>
        string.Create(CultureInfo.InvariantCulture, $" Retry-After: {(long)Math.Ceiling(delay.TotalSeconds)}s.");

    /// <summary>Records <paramref name="delay"/> on <paramref name="exception"/> and returns it.</summary>
    public static TException WithDelay<TException>(TException exception, TimeSpan? delay)
        where TException : Exception
    {
        if (delay is { } value)
        {
            exception.Data[NachosExceptionData.RetryAfter] = value;
        }

        return exception;
    }
}
