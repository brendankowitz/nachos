using System.Collections;
using System.Runtime.CompilerServices;

namespace Nachos.Client;

/// <summary>
/// Keeps the call's secrets (<see cref="RedactionSecrets"/>) out of exceptions that did not come from a mapped
/// status: transport failures whose text echoes what a server sent back, for example
/// <c>Received an invalid header name: 'Bearer …'</c>, or the hex dump of an invalid chunk extension.
/// </summary>
/// <remarks>
/// <para>
/// An exception whose chain never mentions a secret is returned as it is, so identity, type, inner exceptions and
/// stack trace survive in the common case, and so does any secret-free inner exception of a chain that is rebuilt.
/// A level that does mention one is rebuilt: the same type where it matters (<see cref="HttpRequestException"/> and
/// <see cref="HttpIOException"/> with their <see cref="HttpRequestError"/> and status, cancellations with their token,
/// <see cref="TimeoutException"/>, <see cref="IOException"/>, <see cref="AggregateException"/>), the message redacted
/// and bounded like server text (<see cref="ErrorMapper"/>), and <see cref="Exception.Data"/> copied with string keys
/// and string values redacted. Entries whose key starts with <c>Nachos.</c> (such as
/// <see cref="NachosExceptionData.RetryAfter"/>) are the library's and are copied untouched; non-string values are
/// copied as they are. A level of any other type becomes an <see cref="IOException"/> whose message starts with the
/// original type name. Rebuilt exceptions have no stack trace of their own. At most <see cref="MaxRebuiltDepth"/>
/// levels are rebuilt; anything deeper in a secret-bearing chain is dropped.
/// </para>
/// <para>
/// Exceptions this library builds from already-redacted parts (the retry handler's wraps, rebuilt copies) are
/// registered with <see cref="MarkSanitized"/> and never examined or rebuilt again, so their library-owned text (the
/// <c>Retry-After</c> suffix, markers) is never rewritten, whatever the secret.
/// </para>
/// </remarks>
internal static class SecretRedaction
{
    /// <summary>The deepest level of an exception chain that is rebuilt.</summary>
    public const int MaxRebuiltDepth = 32;

    private const string LibraryDataPrefix = "Nachos.";

    private static readonly ConditionalWeakTable<Exception, object> Sanitized = [];

    /// <summary>Records that <paramref name="exception"/> was built from redacted parts; returns it.</summary>
    public static TException MarkSanitized<TException>(TException exception)
        where TException : Exception
    {
        Sanitized.AddOrUpdate(exception, Sanitized);
        return exception;
    }

    /// <summary>
    /// True when a message, or a string key or value of a non-library <see cref="Exception.Data"/> entry, anywhere in
    /// the chain (aggregate inner exceptions included, sanitized exceptions excluded) holds a secret.
    /// </summary>
    public static bool Mentions(Exception exception, RedactionSecrets secrets) =>
        !secrets.IsEmpty && Chain(exception).Any(e => secrets.OccursIn(e.Message) || DataMentions(e.Data, secrets));

    /// <summary>
    /// <paramref name="exception"/> itself when its chain never mentions a secret; otherwise a rebuilt chain without
    /// them (see the type remarks).
    /// </summary>
    public static Exception Redact(Exception exception, RedactionSecrets secrets) => Redact(exception, secrets, depth: 0);

    /// <summary><paramref name="text"/> redacted in one pass, then bounded like server text.</summary>
    public static string Text(string text, RedactionSecrets secrets) => ErrorMapper.Bound(secrets.Redact(text));

    private static Exception Redact(Exception exception, RedactionSecrets secrets, int depth)
    {
        if (!Mentions(exception, secrets))
        {
            return exception;
        }

        var deeper = depth + 1 < MaxRebuiltDepth;
        var message = Text(exception.Message, secrets);
        var inner = deeper && exception.InnerException is { } cause ? Redact(cause, secrets, depth + 1) : null;
        Exception copy = exception switch
        {
            AggregateException aggregate => new AggregateException(
                deeper ? aggregate.InnerExceptions.Select(e => Redact(e, secrets, depth + 1)) : []),
            HttpRequestException http => new HttpRequestException(http.HttpRequestError, message, inner, http.StatusCode),
            HttpIOException http => new HttpIOException(http.HttpRequestError, message, inner),
            TaskCanceledException canceled => new TaskCanceledException(message, inner, canceled.CancellationToken),
            OperationCanceledException canceled => new OperationCanceledException(message, inner, canceled.CancellationToken),
            TimeoutException => new TimeoutException(message, inner),
            IOException => new IOException(message, inner),
            _ => new IOException($"{exception.GetType().FullName}: {message}", inner),
        };

        foreach (DictionaryEntry entry in exception.Data)
        {
            if (IsLibraryKey(entry.Key))
            {
                copy.Data[entry.Key] = entry.Value;
                continue;
            }

            var key = entry.Key is string text ? secrets.Redact(text) : entry.Key;
            copy.Data[key] = entry.Value is string value ? Text(value, secrets) : entry.Value;
        }

        return MarkSanitized(copy);
    }

    private static bool IsLibraryKey(object key) =>
        key is string text && text.StartsWith(LibraryDataPrefix, StringComparison.Ordinal);

    private static bool DataMentions(IDictionary data, RedactionSecrets secrets)
    {
        foreach (DictionaryEntry entry in data)
        {
            if (IsLibraryKey(entry.Key))
            {
                continue;
            }

            if ((entry.Key is string key && secrets.OccursIn(key)) || (entry.Value is string value && secrets.OccursIn(value)))
            {
                return true;
            }
        }

        return false;
    }

    // Iterative, so a very deep chain cannot overflow the stack; sanitized exceptions and their subtrees are skipped.
    private static IEnumerable<Exception> Chain(Exception root)
    {
        var pending = new Stack<Exception>([root]);
        while (pending.TryPop(out var current))
        {
            if (Sanitized.TryGetValue(current, out _))
            {
                continue;
            }

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
