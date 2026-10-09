using System.Collections;

namespace Nachos.Client;

/// <summary>
/// Keeps the bearer value out of exceptions that did not come from a mapped status: transport failures whose text
/// echoes what a server sent back, for example <c>Received an invalid header name: 'Bearer …'</c> when a server
/// reflects the <c>Authorization</c> header into a malformed response line or trailer.
/// </summary>
/// <remarks>
/// An exception whose chain never mentions a secret is returned as it is, so identity, type, inner exceptions and
/// stack trace survive in the common case. One that does is rebuilt level by level: the same type where it matters
/// (<see cref="HttpRequestException"/> with its <see cref="HttpRequestException.HttpRequestError"/> and
/// <see cref="HttpRequestException.StatusCode"/>, cancellations with their token, <see cref="TimeoutException"/>,
/// <see cref="IOException"/>, <see cref="AggregateException"/>), each message redacted and bounded like server text
/// (<see cref="ErrorMapper"/>), and <see cref="Exception.Data"/> copied with string keys and values redacted. A level of
/// any other type becomes an <see cref="IOException"/> naming the original type. The rebuilt exceptions have no stack
/// trace of their own.
/// </remarks>
internal static class SecretRedaction
{
    /// <summary>The bearer value of <paramref name="request"/>'s <c>Authorization</c> header, as a secret list.</summary>
    public static string[] Secrets(HttpRequestMessage request) =>
        request.Headers.Authorization?.Parameter is { Length: > 0 } parameter ? [parameter] : [];

    /// <summary>True when a message, or a string key or value of <see cref="Exception.Data"/>, anywhere in the chain contains a secret.</summary>
    public static bool Mentions(Exception exception, IReadOnlyCollection<string> secrets) =>
        secrets.Count > 0 && Chain(exception).Any(e => Contains(e.Message, secrets) || DataMentions(e.Data, secrets));

    /// <summary>
    /// <paramref name="exception"/> itself when its chain never mentions a secret; otherwise a rebuilt chain without
    /// them (see the type remarks).
    /// </summary>
    public static Exception Redact(Exception exception, IReadOnlyCollection<string> secrets)
    {
        if (!Mentions(exception, secrets))
        {
            return exception;
        }

        var message = Text(exception.Message, secrets);
        var inner = exception.InnerException is { } cause ? Redact(cause, secrets) : null;
        Exception copy = exception switch
        {
            AggregateException aggregate => new AggregateException(aggregate.InnerExceptions.Select(e => Redact(e, secrets))),
            HttpRequestException http => new HttpRequestException(http.HttpRequestError, message, inner, http.StatusCode),
            TaskCanceledException canceled => new TaskCanceledException(message, inner, canceled.CancellationToken),
            OperationCanceledException canceled => new OperationCanceledException(message, inner, canceled.CancellationToken),
            TimeoutException => new TimeoutException(message, inner),
            IOException => new IOException(message, inner),
            _ => new IOException($"{exception.GetType().FullName}: {message}", inner),
        };

        foreach (DictionaryEntry entry in exception.Data)
        {
            var key = entry.Key is string text ? Text(text, secrets) : entry.Key;
            copy.Data[key] = entry.Value is string value ? Text(value, secrets) : entry.Value;
        }

        return copy;
    }

    /// <summary><paramref name="text"/> with every secret replaced by <see cref="ErrorMapper.Redacted"/>, then bounded.</summary>
    public static string Text(string text, IReadOnlyCollection<string> secrets) =>
        ErrorMapper.Sanitize(
            secrets.Aggregate(text, (current, secret) => current.Replace(secret, ErrorMapper.Redacted, StringComparison.Ordinal)),
            secret: null);

    private static bool Contains(string text, IReadOnlyCollection<string> secrets) =>
        secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal));

    private static bool DataMentions(IDictionary data, IReadOnlyCollection<string> secrets)
    {
        foreach (DictionaryEntry entry in data)
        {
            if ((entry.Key is string key && Contains(key, secrets)) || (entry.Value is string value && Contains(value, secrets)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Exception> Chain(Exception root)
    {
        var pending = new Stack<Exception>([root]);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}
