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
/// cap), it is rethrown as an <see cref="HttpRequestException"/> whose <see cref="HttpRequestException.StatusCode"/>
/// is the received status and whose inner exception is the read failure. Any other exception from the read (a
/// cancellation, or a <see cref="TimeoutException"/> or <see cref="TaskCanceledException"/> from a timeout below this
/// handler) surfaces unchanged, without the status. Requests the route rules never replay (for example an unkeyed
/// message create) are not affected: they are sent once and never reach this logic.
/// </description></item>
/// <item><description>
/// A <c>Retry-After</c> longer than <see cref="MaxRetryAfter"/> (30 s) is not waited out; the error reaches the caller
/// with the requested delay. For a status the response is returned at once, header included, and
/// <see cref="NachosHttpClient"/> maps it to an exception carrying the delay (see
/// <see cref="NachosExceptionData.RetryAfter"/>). For a body failure the wrapped <see cref="HttpRequestException"/>
/// above carries the same data entry and message suffix. Mapped status responses and body failures wrapped by this
/// handler carry the delay, including the last one after the attempts are exhausted; a failure that surfaces raw
/// (for example a timeout below this handler) does not.
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
/// <see cref="HttpClient.Timeout"/> bounds the whole call, retries and backoff included, and is never retried.
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

    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;
    private readonly long _maxResponseBufferSize;

    public RetryHandler()
        : this(TimeProvider.System)
    {
    }

    public RetryHandler(TimeProvider timeProvider)
        : this(timeProvider, delay: null, Random.Shared.NextDouble)
    {
    }

    /// <param name="timeProvider">Clock for <c>Retry-After</c> dates and, by default, for delays.</param>
    /// <param name="delay">The wait between attempts; null waits on <paramref name="timeProvider"/>.</param>
    /// <param name="jitter">A source of values in [0, 1) that scales the backoff ceiling.</param>
    /// <param name="maxResponseBufferSize">Largest response body buffered for a retryable request.</param>
    internal RetryHandler(
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Func<double> jitter,
        long maxResponseBufferSize = DefaultMaxResponseBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBufferSize);
        _maxResponseBufferSize = maxResponseBufferSize;
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(jitter);
        _timeProvider = timeProvider;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, timeProvider, ct));
        _jitter = jitter;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsRetryable(request))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
        var canRetry = attempt < MaxAttempts;

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(copy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (canRetry && IsTransient(ex, cancellationToken))
        {
            return (null, Backoff(attempt));
        }

        var status = response.StatusCode;
        try
        {
            // The inner handler returns once headers arrive; HttpClient would read the body after this handler
            // returns, outside the retry loop. Reading it here keeps a reset mid-body (after the server committed)
            // inside the loop, where a keyed request can be replayed.
            await response.Content.LoadIntoBufferAsync(_maxResponseBufferSize, cancellationToken).ConfigureAwait(false);
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

            // A read failure keeps the status it followed and any delay the server asked for (spec §16);
            // cancellations and anything else surface unchanged.
            if (ex is HttpRequestException or IOException)
            {
                var error = (ex as HttpRequestException)?.HttpRequestError ?? HttpRequestError.ResponseEnded;
                var message = retryAfter is { } delay ? ex.Message + RetryAfterHeader.Suffix(delay) : ex.Message;
                throw RetryAfterHeader.WithDelay(new HttpRequestException(error, message, ex, status), retryAfter);
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

    // A cancellation is transient only when it is not the caller's: a timeout raised below this handler surfaces
    // as OperationCanceledException while the caller's token is still live. HttpClient.Timeout is different: it
    // covers the whole call including every retry, cancels the token this handler receives, and so is never
    // retried. TODO(task-12-attempt-timeout): part B adds a per-attempt timeout handler below RetryHandler in the
    // AddNachosClient pipeline so a single stalled attempt can be retried within the overall HttpClient.Timeout.
    // A configured limit (such as the response buffer cap) fails the same way on every attempt, so it never retries.
    private static bool IsTransient(Exception ex, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested &&
        ex is not HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded } &&
        ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException;

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

    // Full jitter: a uniform wait in [0, BaseDelay * 2^(attempt-1)).
    private TimeSpan Backoff(int failedAttempt) =>
        BaseDelay * (Math.Pow(2, failedAttempt - 1) * _jitter());

    private async Task WaitAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _delay(wait, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
