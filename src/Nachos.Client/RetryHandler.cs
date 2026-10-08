using System.Net;

namespace Nachos.Client;

/// <summary>
/// Operation-aware retries (spec §16): up to <see cref="MaxAttempts"/> attempts on 429, 5xx (except 501),
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
/// The body is buffered once and every attempt is a fresh copy of the original request with the same headers,
/// so a replay carries the same <c>Idempotency-Key</c> and identical bytes. A <c>Retry-After</c> longer than
/// <see cref="MaxRetryAfter"/> is not waited out: the response is returned to the caller instead.
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

    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;

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
    internal RetryHandler(TimeProvider timeProvider, Func<TimeSpan, CancellationToken, Task>? delay, Func<double> jitter)
    {
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
            var attemptRequest = Copy(request, body);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex, cancellationToken))
            {
                attemptRequest.Dispose();
                await WaitAsync(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (attempt >= MaxAttempts || !IsTransient(response.StatusCode))
            {
                return response;
            }

            var wait = RetryAfter(response) ?? Backoff(attempt);
            if (wait > MaxRetryAfter)
            {
                return response;
            }

            response.Dispose();
            attemptRequest.Dispose();
            await WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(HttpRequestMessage request) =>
        request.Options.TryGetValue(RouteTemplate, out var template) &&
        RetryClassifier.IsRetryable(request.Method, template, HasIdempotencyKey(request));

    private static bool HasIdempotencyKey(HttpRequestMessage request) =>
        request.Headers.TryGetValues(IdempotencyKeyHeader, out var values) &&
        values.Any(v => !string.IsNullOrWhiteSpace(v));

    // A cancellation is transient only when it is not the caller's: an inner timeout surfaces as
    // OperationCanceledException while the caller's token is still live.
    private static bool IsTransient(Exception ex, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested &&
        ex is HttpRequestException or TimeoutException or OperationCanceledException;

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests ||
        ((int)status >= 500 && status != HttpStatusCode.NotImplemented);

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

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
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
