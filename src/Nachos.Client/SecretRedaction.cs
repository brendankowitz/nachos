using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Nachos.Client;

/// <summary>
/// Decides what a transport failure may say before it leaves the client's handlers: failures whose text can repeat
/// what the server sent are replaced by a fixed message (fail closed); the few whose text cannot are kept, with the
/// call's secrets redacted from them as a second layer (<see cref="RedactionSecrets"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why fail closed.</b> A server that reflects the request's credentials into a malformed response makes the
/// transport's own exception text carry them, in forms no matcher can enumerate: plain, hex-dumped as an invalid chunk
/// extension after the leading hex digits were consumed as the chunk size, split across lines, and so on. So the rule
/// is by exception type, not by content.
/// </para>
/// <para>
/// <b>Kept</b>: a chain whose every level is a <see cref="SocketException"/>, <see cref="TimeoutException"/>,
/// <see cref="OperationCanceledException"/> (<see cref="TaskCanceledException"/> included),
/// <see cref="ObjectDisposedException"/>, <see cref="AggregateException"/>, an <see cref="HttpRequestException"/> whose
/// <see cref="HttpRequestException.HttpRequestError"/> is <see cref="HttpRequestError.ConnectionError"/> or
/// <see cref="HttpRequestError.NameResolutionError"/>, or an exception this library built from sanitized parts
/// (<see cref="MarkSanitized"/>). Their text is framework or library text about the connection, so "connection
/// refused" and timeouts stay informative. <see cref="Sanitize"/> returns the same instance when nothing in the chain
/// mentions a secret and no level is an <see cref="HttpRequestException"/>. A level that mentions a secret is rebuilt
/// with it redacted (the same type for cancellations and timeouts, keeping the token; any other type becomes an
/// <see cref="IOException"/> whose message starts with the original type name), with string <see cref="Exception.Data"/>
/// keys and values redacted except <c>Nachos.</c> entries. An <see cref="HttpRequestException"/> level is always
/// rebuilt, keeping its error, status and (redacted) inner chain but with the fixed text of
/// <see cref="ConnectionMessage"/>: .NET's own text names the target host and port, and a followed redirect lets the
/// server choose that host (a hostname can spell the request's credentials in hex), so the host is withheld even when
/// no secret is found in it. Secret-free levels of the other kept types keep their identity. At most
/// <see cref="MaxRebuiltDepth"/> levels are rebuilt.
/// </para>
/// <para>
/// <b>Replaced</b>: everything else, unknown types included (<see cref="HttpIOException"/>, other
/// <see cref="IOException"/>s, an <see cref="HttpRequestException"/> with any other error, <see cref="FormatException"/>,
/// …). It becomes an <see cref="HttpRequestException"/> with the <see cref="HttpRequestError"/> and status of the first
/// <see cref="HttpRequestException"/> or <see cref="HttpIOException"/> in its chain, only the <c>Nachos.</c> entries of
/// its <see cref="Exception.Data"/>, no inner exception, and the fixed text of <see cref="CannedMessage"/>. A
/// cancellation, timeout or aggregate wrapped around such a chain keeps its own type, token and (redacted) message, and
/// gets the replaced chain as its inner exception.
/// </para>
/// </remarks>
internal static class SecretRedaction
{
    /// <summary>The deepest level of an exception chain that is rebuilt.</summary>
    public const int MaxRebuiltDepth = 32;

    private const string LibraryDataPrefix = "Nachos.";

    private static readonly ConditionalWeakTable<Exception, object> Sanitized = [];

    /// <summary>Records that <paramref name="exception"/> was built from sanitized parts; returns it.</summary>
    public static TException MarkSanitized<TException>(TException exception)
        where TException : Exception
    {
        Sanitized.AddOrUpdate(exception, Sanitized);
        return exception;
    }

    /// <summary>
    /// The exception to surface in place of <paramref name="exception"/>: the same instance when it may be shown as it
    /// is, otherwise a rebuilt or replaced one (see the type remarks).
    /// </summary>
    public static Exception Sanitize(Exception exception, RedactionSecrets secrets) => Sanitize(exception, secrets, depth: 0);

    /// <summary>A replacement failure: the fixed text, then <paramref name="suffix"/>; no inner exception.</summary>
    public static HttpRequestException Replace(HttpRequestError error, HttpStatusCode? status, string suffix = "") =>
        MarkSanitized(new HttpRequestException(error, CannedMessage(error) + suffix, inner: null, status));

    /// <summary>The fixed text of a replaced failure: a static sentence and the error's enum name, nothing else.</summary>
    public static string CannedMessage(HttpRequestError error) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The Nachos HTTP exchange failed ({error}). The transport's own description is withheld because it can repeat what the server sent.");

    /// <summary>
    /// The fixed text of a kept connection failure: a static sentence, the error's enum name and the
    /// <see cref="SocketError"/> of the first <see cref="SocketException"/> beneath it (when there is one); never the
    /// target host and port.
    /// </summary>
    public static string ConnectionMessage(HttpRequestError error, SocketError? socketError) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The Nachos HTTP connection failed ({error}{(socketError is { } code ? $", {code}" : string.Empty)}). The target host and port are withheld because a redirect lets the server choose them.");

    /// <summary>
    /// The first <see cref="HttpRequestError"/> other than <see cref="HttpRequestError.Unknown"/> carried by an
    /// <see cref="HttpRequestException"/> or <see cref="HttpIOException"/> in the chain (else
    /// <see cref="HttpRequestError.Unknown"/>), and the first status an <see cref="HttpRequestException"/> carries.
    /// </summary>
    public static (HttpRequestError Error, HttpStatusCode? Status) Classify(Exception exception)
    {
        var error = HttpRequestError.Unknown;
        HttpStatusCode? status = null;
        foreach (var level in Chain(exception, skipSanitized: false))
        {
            switch (level)
            {
                case HttpRequestException http:
                    status ??= http.StatusCode;
                    error = error == HttpRequestError.Unknown ? http.HttpRequestError : error;
                    break;
                case HttpIOException io:
                    error = error == HttpRequestError.Unknown ? io.HttpRequestError : error;
                    break;
            }
        }

        return (error, status);
    }

    private static Exception Sanitize(Exception exception, RedactionSecrets secrets, int depth)
    {
        if (Sanitized.TryGetValue(exception, out _))
        {
            return exception;
        }

        if (Chain(exception, skipSanitized: true).All(IsSafeLevel))
        {
            return Redact(exception, secrets, depth);
        }

        var deeper = depth + 1 < MaxRebuiltDepth;
        var message = Text(exception.Message, secrets);
        Exception? Inner() => deeper && exception.InnerException is { } cause ? Sanitize(cause, secrets, depth + 1) : null;
        return exception switch
        {
            TaskCanceledException canceled => MarkSanitized(new TaskCanceledException(message, Inner(), canceled.CancellationToken)),
            OperationCanceledException canceled => MarkSanitized(new OperationCanceledException(message, Inner(), canceled.CancellationToken)),
            TimeoutException => MarkSanitized(new TimeoutException(message, Inner())),
            AggregateException aggregate => MarkSanitized(new AggregateException(
                deeper ? aggregate.InnerExceptions.Select(e => Sanitize(e, secrets, depth + 1)) : [])),
            _ => Replaced(exception),
        };
    }

    private static HttpRequestException Replaced(Exception exception)
    {
        var (error, status) = Classify(exception);
        var replacement = Replace(error, status);
        foreach (DictionaryEntry entry in exception.Data)
        {
            if (IsLibraryKey(entry.Key))
            {
                replacement.Data[entry.Key] = entry.Value;
            }
        }

        return replacement;
    }

    // A level whose own text is framework or library text about the connection, never bytes the server sent.
    private static bool IsSafeLevel(Exception level) => level switch
    {
        HttpRequestException http => http.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError,
        SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException or AggregateException => true,
        _ => false,
    };

    // Second layer, for a chain of safe levels only: rebuild the levels that mention a secret, and every
    // HttpRequestException level (its text names the host, see the type remarks).
    private static Exception Redact(Exception exception, RedactionSecrets secrets, int depth)
    {
        if (!NeedsRebuild(exception, secrets))
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
            HttpRequestException http => new HttpRequestException(
                http.HttpRequestError, ConnectionMessage(http.HttpRequestError, FirstSocketError(http)), inner, http.StatusCode),
            TaskCanceledException canceled => new TaskCanceledException(message, inner, canceled.CancellationToken),
            OperationCanceledException canceled => new OperationCanceledException(message, inner, canceled.CancellationToken),
            TimeoutException => new TimeoutException(message, inner),
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

    private static bool NeedsRebuild(Exception exception, RedactionSecrets secrets) =>
        Chain(exception, skipSanitized: true).Any(e =>
            e is HttpRequestException ||
            (!secrets.IsEmpty && (secrets.OccursIn(e.Message) || DataMentions(e.Data, secrets))));

    private static SocketError? FirstSocketError(Exception exception) =>
        Chain(exception, skipSanitized: false).OfType<SocketException>().FirstOrDefault()?.SocketErrorCode;

    private static string Text(string text, RedactionSecrets secrets) => ErrorMapper.Bound(secrets.Redact(text));

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

    // Iterative, so a very deep chain cannot overflow the stack.
    private static IEnumerable<Exception> Chain(Exception root, bool skipSanitized)
    {
        var pending = new Stack<Exception>([root]);
        while (pending.TryPop(out var current))
        {
            if (skipSanitized && Sanitized.TryGetValue(current, out _))
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
