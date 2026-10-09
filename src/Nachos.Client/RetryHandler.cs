using System.Globalization;
using System.Net;

namespace Nachos.Client;

/// <summary>
/// Operation-aware retries (spec §16): up to <see cref="MaxAttempts"/> attempts on 408, 429, 5xx (except 501),
/// transport failures, and timeouts that are not the caller's cancellation. Delays use exponential backoff with
/// full jitter, or the server's <c>Retry-After</c> when present.
/// </summary>
/// <remarks>
/// <para>
/// A request is retried only when it carries its wire route template in <see cref="RouteTemplate"/> (set by
/// <see cref="NachosHttpClient"/>) and <see cref="RetryClassifier.IsRetryable"/> allows it. Anything else is sent
/// exactly once.
/// </para>
/// <para>
/// <b>Retry policy (spec §16).</b>
/// <list type="bullet">
/// <item><description>
/// Retryable statuses are 408, 429 and 5xx other than 501. 501 is never retried: "not implemented" is a permanent
/// answer for the route, not a transient one.
/// </description></item>
/// <item><description>
/// Status precedence: once a status line is received, that status decides. After a non-retryable status (any 3xx, any
/// 4xx other than 408/429, and 501) a failure while reading the body is final and is not resent. After a 2xx or a
/// retryable status the body failure is retried, so a lost successful response to a keyed mutation is replayed
/// safely. When a body read failure that is an <see cref="HttpRequestException"/> or an <see cref="IOException"/>
/// surfaces from this handler (final status, attempts exhausted, the buffer cap, or a <c>Retry-After</c> over the
/// cap), it is replaced by an <see cref="HttpRequestException"/> whose <see cref="HttpRequestException.StatusCode"/>
/// is the received status, whose <see cref="HttpRequestException.HttpRequestError"/> is the read failure's, and whose
/// message is fixed text with no inner exception: the read failure's own text describes server bytes and can repeat
/// the request's credentials (see <see cref="SecretRedaction"/>). This handler's own attempt timeout (below) keeps the
/// status too, with a <see cref="TimeoutException"/> as the inner exception. The caller's cancellation surfaces as an
/// <see cref="OperationCanceledException"/> carrying the caller's token, without the status; it is the transport's own
/// instance when nothing in it can repeat server bytes, otherwise a rebuilt one of the same type and token (with a
/// real socket, <see cref="HttpClient"/> reports a cancellation as a <see cref="TaskCanceledException"/> whose inner
/// exception is the raw transport failure, so the rebuilt form is the usual one). Requests the route rules never replay
/// (for example an unkeyed message create) are not affected: they are sent once and never reach this logic.
/// </description></item>
/// <item><description>
/// A <c>Retry-After</c> longer than <see cref="MaxRetryAfter"/> (30 s) is not waited out; the error reaches the caller
/// with the requested delay. For a status the response is returned at once, header included, and
/// <see cref="NachosHttpClient"/> maps it to an exception carrying the delay (see
/// <see cref="NachosExceptionData.RetryAfter"/>). For a body failure the wrapped <see cref="HttpRequestException"/>
/// above carries the same data entry and message suffix. Mapped status responses and body failures wrapped by this
/// handler carry the delay, including the last one after the attempts are exhausted (a timed-out body read included);
/// a failure that surfaces raw (for example a timeout below this handler) does not.
/// </description></item>
/// <item><description>
/// Routes <see cref="RetryClassifier"/> classifies as never retried (keys, grants, adding sessions to a scope until
/// its backfill enqueue is shown idempotent) are sent once whatever the status.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// For a retryable request the response body is read inside the retry loop, so a failure while reading it is
/// retried like a transport failure (honouring <c>Retry-After</c> when the failed response carried one), subject to
/// the status precedence above. Non-retryable requests pass through untouched (streaming preserved).
/// </para>
/// <para>
/// <b>Ownership.</b> Each attempt sends a copy of the caller's request. The handler owns every copy and disposes each
/// one before <c>SendAsync</c> returns or throws, on every path (success, retry, exhaustion, exception, cancellation).
/// A response that is not handed back is disposed before the next attempt. The returned response's
/// <see cref="HttpResponseMessage.RequestMessage"/> is the caller's original request, which the caller still owns and
/// disposes. A non-retryable request is sent as is, without a copy. Consequence: when a handler below this one follows
/// a redirect (SocketsHttpHandler rewrites the URI of the request it was given), the rewrite lands on the disposed
/// copy, so the returned <see cref="HttpResponseMessage.RequestMessage"/> keeps the caller's pre-redirect
/// <see cref="HttpRequestMessage.RequestUri"/>; the final URI is not carried back.
/// </para>
/// <para>
/// That buffer is capped at <see cref="DefaultMaxResponseBufferSize"/>; a larger body fails with
/// <see cref="HttpRequestError.ConfigurationLimitExceeded"/> and is not retried. On retryable routes the caller's
/// <see cref="HttpClient.MaxResponseContentBufferSize"/> is not consulted, because HttpClient skips its own limit
/// for content that is already buffered.
/// </para>
/// <para>
/// <b>Attempt timeout.</b> Each attempt runs under the caller's token linked with a timer of the configured attempt
/// timeout (default <see cref="DefaultAttemptTimeout"/>, 30 s; <see cref="Timeout.InfiniteTimeSpan"/> disables it),
/// created on the injected <see cref="TimeProvider"/>. For a retryable request the attempt is the send plus buffering
/// the response body; a new attempt gets a fresh timer, and backoff waits are not counted. A timed-out attempt is a
/// transient failure exactly like a transport failure: it is retried (up to <see cref="MaxAttempts"/>, with backoff)
/// only when the request is retryable and, if a status line was already received, only when status precedence allows
/// it. When it is not retried (attempts exhausted, a final status, a <c>Retry-After</c> over the cap, or a request
/// that is never retried) it surfaces as an <see cref="HttpRequestException"/> with
/// <see cref="HttpRequestError.Unknown"/> whose inner exception is a <see cref="TimeoutException"/> (itself wrapping
/// the cancellation), whose <see cref="HttpRequestException.StatusCode"/> is the received status if a status line
/// arrived, and which carries the <see cref="NachosExceptionData.RetryAfter"/> entry and message suffix when that
/// response had a parseable <c>Retry-After</c>. A request that is never retried is sent as is, so its attempt timeout
/// bounds only the time to response headers; its body is streamed to <see cref="HttpClient"/> after this handler
/// returns. The caller's cancellation always wins: if the caller's token has fired, the cancellation surfaces with
/// that token (the same instance, or a rebuilt one of the same type when its cause had to be sanitized; see above) and
/// is never retried or reported as a timeout.
/// </para>
/// <para>
/// <b>Failure text.</b> Every exception that leaves this handler goes through <see cref="SecretRedaction.Sanitize"/>:
/// known-safe connection failures, timeouts and cancellations are kept (with the request's bearer value redacted from
/// them, and a connection failure's text rebuilt without the target host and port), anything that can carry server
/// bytes is replaced by fixed text naming its <see cref="HttpRequestError"/>. A failure already replaced below this
/// handler (by the primary-handler wrapper of <c>AddNachosClient</c>) is retried only if its original type would have
/// been: the replacement is an <see cref="HttpRequestException"/>, but a replaced <see cref="InvalidOperationException"/>
/// from a handler is still sent once.
/// </para>
/// <para>
/// <see cref="HttpClient.Timeout"/> (100 s unless changed) bounds the whole call, retries and backoff included, and
/// is never retried: it cancels the token this handler receives, which counts as the caller's cancellation.
/// </para>
/// <para>
/// The request body is buffered once and every attempt is a fresh copy of the original request with the same headers,
/// so a replay carries the same <c>Idempotency-Key</c> and identical bytes.
/// </para>
/// </remarks>
public sealed class RetryHandler : DelegatingHandler
{
    /// <summary>Total attempts, including the first.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The wire route template of the request, for example <c>/v3/workspaces/{workspace_id}</c>.</summary>
    public static readonly HttpRequestOptionsKey<string> RouteTemplate = new("Nachos.RouteTemplate");

    /// <summary>The longest server-requested wait that is honoured before retrying.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The backoff ceiling after the first failed attempt; it doubles per attempt.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Default cap on a buffered response body for retryable requests.</summary>
    public const long DefaultMaxResponseBufferSize = 64L * 1024 * 1024;

    /// <summary>
    /// Default bound on one attempt (send plus, for a retryable request, buffering the body). Three timed-out attempts
    /// with jittered backoff (at most 0.5 s + 1 s) fit inside <see cref="HttpClient"/>'s default 100 s
    /// <see cref="HttpClient.Timeout"/>, but honoured <c>Retry-After</c> waits do not always: the worst case is three
    /// 30 s attempts plus two 30 s waits, 150 s. When <see cref="HttpClient.Timeout"/> runs out first, the call ends
    /// as <see cref="HttpClient"/>'s <see cref="TaskCanceledException"/> (inner <see cref="TimeoutException"/>), without
    /// the status or the requested delay. Raise <see cref="HttpClient.Timeout"/> if every honoured wait must complete.
    /// </summary>
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(30);

    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;
    private readonly long _maxResponseBufferSize;
    private readonly TimeSpan _attemptTimeout;

    /// <summary>Uses the system clock and <see cref="DefaultAttemptTimeout"/>.</summary>
    public RetryHandler()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Uses <paramref name="timeProvider"/> and <see cref="DefaultAttemptTimeout"/>.</summary>
    public RetryHandler(TimeProvider timeProvider)
        : this(timeProvider, DefaultAttemptTimeout)
    {
    }

    /// <param name="timeProvider">Clock for backoff waits, <c>Retry-After</c> dates and the attempt timeout.</param>
    /// <param name="attemptTimeout">
    /// Bound on each attempt; <see cref="Timeout.InfiniteTimeSpan"/> disables it. See <see cref="IsValidAttemptTimeout"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attemptTimeout"/> is out of range.</exception>
    public RetryHandler(TimeProvider timeProvider, TimeSpan attemptTimeout)
        : this(timeProvider, delay: null, Random.Shared.NextDouble, attemptTimeout: attemptTimeout)
    {
    }

    /// <param name="timeProvider">Clock for <c>Retry-After</c> dates, the attempt timeout and, by default, delays.</param>
    /// <param name="delay">The wait between attempts; null waits on <paramref name="timeProvider"/>.</param>
    /// <param name="jitter">A source of values in [0, 1) that scales the backoff ceiling.</param>
    /// <param name="maxResponseBufferSize">Largest response body buffered for a retryable request.</param>
    /// <param name="attemptTimeout">Bound on each attempt; null is <see cref="DefaultAttemptTimeout"/>.</param>
    internal RetryHandler(
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Func<double> jitter,
        long maxResponseBufferSize = DefaultMaxResponseBufferSize,
        TimeSpan? attemptTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBufferSize);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(jitter);
        var timeout = attemptTimeout ?? DefaultAttemptTimeout;
        if (!IsValidAttemptTimeout(timeout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptTimeout), timeout, "The attempt timeout must be positive and at most int.MaxValue milliseconds, or infinite.");
        }

        _maxResponseBufferSize = maxResponseBufferSize;
        _timeProvider = timeProvider;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, timeProvider, ct));
        _jitter = jitter;
        _attemptTimeout = timeout;
    }

    /// <summary>
    /// True for <see cref="Timeout.InfiniteTimeSpan"/> (no attempt timeout) or a positive span of at most
    /// <see cref="int.MaxValue"/> milliseconds, the same range <see cref="HttpClient.Timeout"/> accepts.
    /// </summary>
    public static bool IsValidAttemptTimeout(TimeSpan attemptTimeout) =>
        attemptTimeout == Timeout.InfiniteTimeSpan ||
        (attemptTimeout > TimeSpan.Zero && attemptTimeout.TotalMilliseconds <= int.MaxValue);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Whatever surfaces from here (raw transport failures included) never carries the request's bearer value, so
        // handlers and loggers above this one cannot leak it either. The secrets (the value plus two hex forms) are
        // built on the failure path only; a successful call allocates nothing for them.
        try
        {
            return await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safe = SecretRedaction.Sanitize(ex, RedactionSecrets.FromAuthorization(request));
            if (ReferenceEquals(safe, ex))
            {
                throw;
            }

            throw safe;
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsRetryable(request))
        {
            // Sent once, as is: the attempt timeout bounds the time to response headers only, because the body is
            // streamed to HttpClient after this handler returns.
            using var once = new AttemptScope(_timeProvider, _attemptTimeout, cancellationToken);
            try
            {
                return await base.SendAsync(request, once.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (once.TimedOut)
            {
                throw AttemptTimedOut(ex, request, status: null, retryAfter: null);
            }
        }

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            var (response, wait) = await AttemptAsync(request, body, attempt, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                return response;
            }

            await WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends one copy of <paramref name="original"/>. Returns the response to hand back (its
    /// <see cref="HttpResponseMessage.RequestMessage"/> set to <paramref name="original"/>), or no response and the wait
    /// before the next attempt; anything else throws. The copy is disposed before this returns or throws, and a response
    /// that is not handed back is disposed too.
    /// </summary>
    private async Task<(HttpResponseMessage? Final, TimeSpan Wait)> AttemptAsync(
        HttpRequestMessage original, byte[]? body, int attempt, CancellationToken cancellationToken)
    {
        using var copy = Copy(original, body);
        using var scope = new AttemptScope(_timeProvider, _attemptTimeout, cancellationToken);
        var canRetry = attempt < MaxAttempts;

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(copy, scope.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (canRetry && IsTransient(ex, cancellationToken))
        {
            return (null, Backoff(attempt));
        }
        catch (OperationCanceledException ex) when (scope.TimedOut)
        {
            throw AttemptTimedOut(ex, original, status: null, retryAfter: null);
        }

        var status = response.StatusCode;
        try
        {
            // The inner handler returns once headers arrive; HttpClient would read the body after this handler
            // returns, outside the retry loop. Reading it here keeps a reset mid-body (after the server committed)
            // inside the loop, where a keyed request can be replayed.
            await response.Content.LoadIntoBufferAsync(_maxResponseBufferSize, scope.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var retryAfter = RetryAfterHeader.Delay(response, _timeProvider);
            response.Dispose();

            // Status precedence: the received status decides whether a body failure may be retried.
            if (canRetry && IsTransient(ex, cancellationToken) && BodyFailureIsRetryable(status))
            {
                var failureWait = retryAfter ?? Backoff(attempt);
                if (failureWait <= MaxRetryAfter)
                {
                    return (null, failureWait);
                }
            }

            // A read failure keeps the status it followed and any delay the server asked for (spec §16); so does an
            // attempt timeout. The caller's cancellation surfaces unchanged; anything else is sanitized by SendAsync.
            if (ex is OperationCanceledException canceled && scope.TimedOut)
            {
                throw AttemptTimedOut(canceled, original, status, retryAfter);
            }

            if (ex is HttpRequestException or IOException)
            {
                // A body read failure's text is the transport describing server bytes (a malformed chunk or trailer can
                // repeat the request's credentials), so it is replaced by fixed text; the library suffix follows it.
                var (error, _) = SecretRedaction.Classify(ex);
                if (error == HttpRequestError.Unknown && ex is not HttpRequestException)
                {
                    error = HttpRequestError.ResponseEnded;
                }

                var suffix = retryAfter is { } delay ? RetryAfterHeader.Suffix(delay) : string.Empty;
                throw RetryAfterHeader.WithDelay(SecretRedaction.Replace(error, status, suffix), retryAfter);
            }

            throw;
        }

        if (canRetry && IsRetryableStatus(status))
        {
            var wait = RetryAfterHeader.Delay(response, _timeProvider) ?? Backoff(attempt);
            if (wait <= MaxRetryAfter)
            {
                response.Dispose();
                return (null, wait);
            }
        }

        // The copy is disposed on return; the caller's response must not point at it.
        response.RequestMessage = original;
        return (response, default);
    }

    private static bool IsRetryable(HttpRequestMessage request) =>
        request.Options.TryGetValue(RouteTemplate, out var template) &&
        RetryClassifier.IsRetryable(request.Method, template, HasIdempotencyKey(request));

    private static bool HasIdempotencyKey(HttpRequestMessage request) =>
        request.Headers.TryGetValues(IdempotencyKeyHeader, out var values) &&
        values.Any(v => !string.IsNullOrWhiteSpace(v));

    // A cancellation is transient only when it is not the caller's: this handler's own attempt timeout, or a timeout
    // raised below it, surfaces as OperationCanceledException while the caller's token is still live. HttpClient.Timeout
    // is different: it covers the whole call including every retry, cancels the token this handler receives, and so is
    // never retried. A configured limit (such as the response buffer cap) fails the same way on every attempt, so it
    // never retries. A failure the primary-handler wrapper replaced by fixed text is classified by the type it replaced
    // (an InvalidOperationException from a handler is a bug, not a transient failure), so the wrapper never changes
    // what is retried.
    private static bool IsTransient(Exception ex, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested &&
        ex is not HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded } &&
        IsTransientType(SecretRedaction.ReplacedType(ex) ?? ex.GetType());

    private static bool IsTransientType(Type type) =>
        type.IsAssignableTo(typeof(HttpRequestException)) ||
        type.IsAssignableTo(typeof(IOException)) ||
        type.IsAssignableTo(typeof(TimeoutException)) ||
        type.IsAssignableTo(typeof(OperationCanceledException));

    // Spec §16: 408, 429 and 5xx other than 501. Every other status (2xx, 3xx, the rest of 4xx, 501) is final.
    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        ((int)status >= 500 && status != HttpStatusCode.NotImplemented);

    // Spec §16 status precedence: a body lost after a 2xx or a retryable status may be retried; after any other status
    // that status is final, so the read failure surfaces instead.
    private static bool BodyFailureIsRetryable(HttpStatusCode status) =>
        ((int)status >= 200 && (int)status <= 299) || IsRetryableStatus(status);

    private static HttpRequestMessage Copy(HttpRequestMessage original, byte[]? body)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };
        foreach (var header in original.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in original.Options)
        {
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        }

        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in original.Content!.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return copy;
    }

    // The attempt timeout surfaces like a transport failure: an HttpRequestException (with the received status and any
    // Retry-After delay, as for a body failure) whose inner TimeoutException keeps the cancellation as its cause.
    // The cause is redacted with the request's secrets and the result is marked sanitized, like the body-failure wrap.
    private HttpRequestException AttemptTimedOut(
        OperationCanceledException cause, HttpRequestMessage request, HttpStatusCode? status, TimeSpan? retryAfter)
    {
        var timeout = string.Create(
            CultureInfo.InvariantCulture, $"The Nachos request attempt did not complete within {_attemptTimeout.TotalSeconds:0.###} s.");
        var message = retryAfter is { } delay ? timeout + RetryAfterHeader.Suffix(delay) : timeout;
        var redactedCause = SecretRedaction.Sanitize(cause, RedactionSecrets.FromAuthorization(request));
        return SecretRedaction.MarkSanitized(RetryAfterHeader.WithDelay(
            new HttpRequestException(HttpRequestError.Unknown, message, new TimeoutException(timeout, redactedCause), status), retryAfter));
    }

    // Full jitter: a uniform wait in [0, BaseDelay * 2^(attempt-1)).
    private TimeSpan Backoff(int failedAttempt) =>
        BaseDelay * (Math.Pow(2, failedAttempt - 1) * _jitter());

    private async Task WaitAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _delay(wait, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// One attempt's cancellation: the caller's token linked with a timer on the handler's clock. Disposing it stops the
    /// timer; it never cancels anything itself.
    /// </summary>
    private sealed class AttemptScope : IDisposable
    {
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource? _timeout;
        private readonly CancellationTokenSource? _linked;

        public AttemptScope(TimeProvider clock, TimeSpan attemptTimeout, CancellationToken caller)
        {
            _caller = caller;
            if (attemptTimeout == Timeout.InfiniteTimeSpan)
            {
                Token = caller;
                return;
            }

            _timeout = new CancellationTokenSource(attemptTimeout, clock);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _timeout.Token);
            Token = _linked.Token;
        }

        public CancellationToken Token { get; }

        /// <summary>
        /// True when the attempt's timeout fired and the caller's token did not. The caller's cancellation always wins,
        /// so a call the caller cancelled is never reported as timed out.
        /// </summary>
        public bool TimedOut => _timeout is { IsCancellationRequested: true } && !_caller.IsCancellationRequested;

        public void Dispose()
        {
            _linked?.Dispose();
            _timeout?.Dispose();
        }
    }
}
