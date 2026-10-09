using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// The per-attempt timeout inside <see cref="RetryHandler"/>: each attempt (send plus body buffering) is bounded on the
/// injected <see cref="TimeProvider"/>, so these tests advance a fake clock and never sleep. A timed-out attempt is a
/// transient failure: retried only for a retryable operation and only where status precedence allows, never confused
/// with the caller's cancellation, and surfaced as an <see cref="HttpRequestException"/> whose inner exception is a
/// <see cref="TimeoutException"/>.
/// </summary>
public sealed class AttemptTimeoutTests
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly Uri Base = new("https://nachos.test/");

    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","metadata":{},"created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    private static readonly IReadOnlyList<MessageCreate> Batch = [new("hello", "alice")];

    private readonly FakeTimeProvider _time = new(Now);

    private readonly List<TimeSpan> _delays = [];

    [Fact]
    public async Task StalledAttempt_OnARetryableRoute_IsRetried()
    {
        var server = new StallServer(attempt => attempt == 1 ? null : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        var call = Client(Retry(server)).GetMessageAsync("w1", "s1", "m1");
        await server.FireTimeoutAsync(_time, AttemptTimeout);

        (await call.WaitAsync(TimeSpan.FromSeconds(10))).Id.ShouldBe("m1");
        server.Attempts.ShouldBe(2);
        server.CancelledAttempts.ShouldBe(1);
        _delays.Count.ShouldBe(1);
    }

    [Fact]
    public async Task StalledAttempts_Exhausted_SurfaceAsHttpRequestException_WithTimeoutInner()
    {
        var server = new StallServer(_ => null);

        var call = Client(Retry(server)).GetMessageAsync("w1", "s1", "m1");
        for (var i = 0; i < RetryHandler.MaxAttempts; i++)
        {
            await server.FireTimeoutAsync(_time, AttemptTimeout);
        }

        var ex = await Should.ThrowAsync<HttpRequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        ex.InnerException.ShouldBeOfType<TimeoutException>();
        ex.HttpRequestError.ShouldBe(HttpRequestError.Unknown);
        ex.StatusCode.ShouldBeNull();
        NachosExceptionData.TryGetRetryAfter(ex, out _).ShouldBeFalse();
        server.Attempts.ShouldBe(RetryHandler.MaxAttempts);
        _delays.Count.ShouldBe(RetryHandler.MaxAttempts - 1);
    }

    [Fact]
    public async Task Timeout_IsNotFiredBeforeTheAttemptTimeout()
    {
        var server = new StallServer(attempt => attempt == 1 ? null : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        var call = Client(Retry(server)).GetMessageAsync("w1", "s1", "m1");
        await server.WaitForStallAsync();
        _time.Advance(AttemptTimeout - TimeSpan.FromMilliseconds(1));
        await Task.Yield();

        call.IsCompleted.ShouldBeFalse();
        server.CancelledAttempts.ShouldBe(0);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await call.WaitAsync(TimeSpan.FromSeconds(10));
        server.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task EachAttempt_GetsAFreshTimeout()
    {
        // Attempt 1 takes 20 s of fake time and then fails; attempt 2 must still get the full 30 s of its own.
        var server = new StallServer(attempt =>
        {
            switch (attempt)
            {
                case 1:
                    _time.Advance(TimeSpan.FromSeconds(20));
                    return StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}""");
                case 2:
                    return null;
                default:
                    return StubHandler.Json(HttpStatusCode.OK, MessageJson);
            }
        });

        var call = Client(Retry(server)).GetMessageAsync("w1", "s1", "m1");
        await server.WaitForStallAsync();
        _time.Advance(TimeSpan.FromSeconds(29));
        await Task.Yield();
        call.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));

        await call.WaitAsync(TimeSpan.FromSeconds(10));
        server.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task StalledAttempt_OnANeverRetriedRoute_IsSentOnce_AndSurfacesAsTimeout()
    {
        var server = new StallServer(_ => null);

        var call = Client(Retry(server)).CreateKeyAsync("w1");
        await server.FireTimeoutAsync(_time, AttemptTimeout);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        ex.InnerException.ShouldBeOfType<TimeoutException>();
        ex.StatusCode.ShouldBeNull();
        server.Attempts.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task StalledUnkeyedMessageCreate_IsNeverReplayed()
    {
        var server = new StallServer(_ => null);
        var pipeline = new StripIdempotencyKey { InnerHandler = Retry(server) };

        var call = Client(pipeline).CreateMessagesAsync("w1", "s1", Batch);
        await server.FireTimeoutAsync(_time, AttemptTimeout);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        ex.InnerException.ShouldBeOfType<TimeoutException>();
        server.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task StalledKeyedMessageCreate_IsReplayedWithTheSameKey()
    {
        var server = new StallServer(attempt => attempt == 1 ? null : StubHandler.Json(HttpStatusCode.Created, "[" + MessageJson + "]"));

        var call = Client(Retry(server)).CreateMessagesAsync("w1", "s1", Batch);
        await server.FireTimeoutAsync(_time, AttemptTimeout);

        (await call.WaitAsync(TimeSpan.FromSeconds(10))).Count.ShouldBe(1);
        server.Attempts.ShouldBe(2);
        server.IdempotencyKeys.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task CallerCancellation_DuringAStalledAttempt_SurfacesUntouched_AndIsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var server = new StallServer(_ => null);

        var call = Client(Retry(server)).GetMessageAsync("w1", "s1", "m1", cts.Token);
        await server.WaitForStallAsync();
        await cts.CancelAsync();

        // HttpClient reports anything after the caller cancelled as a cancellation; what this handler controls is that
        // the attempt is neither retried nor waited on.
        await Should.ThrowAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        server.Attempts.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}", "GET")]
    [InlineData("/v3/keys", "POST")]
    public async Task CallerCancellation_WinsWhenTheAttemptTimeoutHasAlsoFired(string template, string method)
    {
        // Both tokens have fired by the time the attempt fails: the attempt timeout first, then the caller's.
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var inner = new DelegateHandler((_, _) =>
        {
            attempts++;
            _time.Advance(AttemptTimeout);
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var invoker = new HttpMessageInvoker(Retry(inner));
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Base, "/v3/x"));
        request.Options.Set(RetryHandler.RouteTemplate, template);

        var caught = await CaptureAsync(invoker.SendAsync(request, cts.Token));

        caught.ShouldBeAssignableTo<OperationCanceledException>();
        caught.ShouldNotBeOfType<HttpRequestException>();
        attempts.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}", "GET")]
    [InlineData("/v3/keys", "POST")]
    public async Task CallerCancellation_AtTheHandler_IsACancellation_NotATimeout(string template, string method)
    {
        // Without HttpClient in between (it reports anything after the caller cancelled as a cancellation), the
        // handler's own exception is visible.
        using var cts = new CancellationTokenSource();
        var server = new StallServer(_ => null);
        using var invoker = new HttpMessageInvoker(Retry(server));
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Base, "/v3/x"));
        request.Options.Set(RetryHandler.RouteTemplate, template);

        var call = invoker.SendAsync(request, cts.Token);
        await server.WaitForStallAsync();
        await cts.CancelAsync();

        (await CaptureAsync(call)).ShouldBeAssignableTo<OperationCanceledException>();
        server.Attempts.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}", "GET", 3)]
    [InlineData("/v3/keys", "POST", 1)]
    public async Task Timeout_AtTheHandler_IsAnHttpRequestException(string template, string method, int attempts)
    {
        var server = new StallServer(_ => null);
        using var invoker = new HttpMessageInvoker(Retry(server));
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Base, "/v3/x"));
        request.Options.Set(RetryHandler.RouteTemplate, template);

        var call = invoker.SendAsync(request, CancellationToken.None);
        for (var i = 0; i < attempts; i++)
        {
            await server.FireTimeoutAsync(_time, AttemptTimeout);
        }

        var ex = (await CaptureAsync(call)).ShouldBeOfType<HttpRequestException>();
        ex.InnerException.ShouldBeOfType<TimeoutException>().InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        ex.Message.ShouldContain("30 s");
        server.Attempts.ShouldBe(attempts);
    }

    [Fact]
    public async Task CallerCancellation_OnANeverRetriedRoute_SurfacesUntouched()
    {
        using var cts = new CancellationTokenSource();
        var server = new StallServer(_ => null);

        var call = Client(Retry(server)).CreateKeyAsync("w1", ct: cts.Token);
        await server.WaitForStallAsync();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        server.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task InfiniteAttemptTimeout_NeverFires()
    {
        using var cts = new CancellationTokenSource();
        var server = new StallServer(_ => null);

        var call = Client(Retry(server, Timeout.InfiniteTimeSpan)).GetMessageAsync("w1", "s1", "m1", cts.Token);
        await server.WaitForStallAsync();
        _time.Advance(TimeSpan.FromDays(365));
        await Task.Yield();

        call.IsCompleted.ShouldBeFalse();
        server.CancelledAttempts.ShouldBe(0);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task BodyStall_AfterA2xx_IsRetried()
    {
        var bodies = new List<StallingStream>();
        var stub = new StubHandler((_, attempt) =>
        {
            if (attempt > 1)
            {
                return StubHandler.Json(HttpStatusCode.OK, MessageJson);
            }

            var stream = new StallingStream(() => _time.Advance(AttemptTimeout));
            bodies.Add(stream);
            return Streamed(HttpStatusCode.OK, stream);
        });

        var message = await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        message.Id.ShouldBe("m1");
        stub.Requests.Count.ShouldBe(2);
        bodies.Single().ReadWasCancelled.ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task BodyStall_AfterAFinalStatus_IsNotRetried_AndKeepsTheStatus(HttpStatusCode status)
    {
        var stub = new StubHandler((_, _) => Streamed(status, new StallingStream(() => _time.Advance(AttemptTimeout))));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        ex.StatusCode.ShouldBe(status);
        ex.InnerException.ShouldBeOfType<TimeoutException>();
        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task BodyStalls_Exhausted_KeepTheLastStatus_AndItsRetryAfter()
    {
        var stub = new StubHandler((_, _) =>
        {
            var response = Streamed(HttpStatusCode.ServiceUnavailable, new StallingStream(() => _time.Advance(AttemptTimeout)));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return response;
        });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        stub.Requests.Count.ShouldBe(RetryHandler.MaxAttempts);
        _delays.ShouldBe([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)]);
        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ex.InnerException.ShouldBeOfType<TimeoutException>();
        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromSeconds(2));
        ex.Message.ShouldEndWith(" Retry-After: 2s.");
    }

    [Fact]
    public async Task BodyStall_WithRetryAfterOverTheCap_SurfacesAtOnce_WithTheDelay()
    {
        var stub = new StubHandler((_, _) =>
        {
            var response = Streamed(HttpStatusCode.TooManyRequests, new StallingStream(() => _time.Advance(AttemptTimeout)));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(40));
            return response;
        });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        stub.Requests.Count.ShouldBe(1);
        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.InnerException.ShouldBeOfType<TimeoutException>();
        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task CallerCancellation_DuringABodyStall_SurfacesUntouched()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHandler((_, _) => Streamed(HttpStatusCode.OK, new StallingStream(cts.Cancel)));

        var ex = await Should.ThrowAsync<OperationCanceledException>(
            () => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1", cts.Token));

        ex.ShouldNotBeOfType<HttpRequestException>();
        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task HttpClientTimeout_StaysTheOverallBound_AndIsNotRetried()
    {
        // The overall HttpClient.Timeout runs on the real clock; a short one fires before the 30 s fake attempt timeout.
        var server = new StallServer(_ => null);
        using var http = new HttpClient(Retry(server)) { Timeout = TimeSpan.FromMilliseconds(200) };
        var client = new NachosHttpClient(http, new NachosClientOptions { BaseAddress = Base });

        var caught = await CaptureAsync(client.GetMessageAsync("w1", "s1", "m1"));

        caught.ShouldBeOfType<TaskCanceledException>().InnerException.ShouldBeOfType<TimeoutException>();
        server.Attempts.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveAttemptTimeout_IsRejected(int seconds)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RetryHandler(TimeProvider.System, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void AttemptTimeoutBeyondTheTimerRange_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RetryHandler(TimeProvider.System, TimeSpan.FromMilliseconds(int.MaxValue + 1L)));
    }

    [Fact]
    public void DefaultAttemptTimeout_Is30Seconds()
    {
        RetryHandler.DefaultAttemptTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    // Caught by hand: Should.ThrowAsync replaces a cancelled task's exception with a fresh TaskCanceledException.
    private static async Task<Exception?> CaptureAsync(Task call)
    {
        try
        {
            await call.WaitAsync(TimeSpan.FromSeconds(10));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private NachosHttpClient Client(HttpMessageHandler pipeline) =>
        new(new HttpClient(pipeline), new NachosClientOptions { BaseAddress = Base }, _time);

    private RetryHandler Retry(HttpMessageHandler inner, TimeSpan? attemptTimeout = null) =>
        new(_time, RecordDelay, () => 0.5, attemptTimeout: attemptTimeout ?? AttemptTimeout) { InnerHandler = inner };

    private static HttpResponseMessage Streamed(HttpStatusCode status, Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    private Task RecordDelay(TimeSpan delay, CancellationToken ct)
    {
        _delays.Add(delay);
        return Task.CompletedTask;
    }

    private sealed class StripIdempotencyKey : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("Idempotency-Key");
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A fake server whose attempts either answer at once or stall (when <c>respond</c> returns null) until the token
    /// the handler passed down is cancelled. A 10 s real-time safety net fails a stall that is never cancelled.
    /// </summary>
    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class StallServer(Func<int, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        private readonly SemaphoreSlim _stalled = new(0);
        private readonly List<string?> _keys = [];
        private int _attempts;
        private int _cancelled;

        public int Attempts => Volatile.Read(ref _attempts);

        public int CancelledAttempts => Volatile.Read(ref _cancelled);

        public IReadOnlyList<string?> IdempotencyKeys
        {
            get
            {
                lock (_keys)
                {
                    return [.. _keys];
                }
            }
        }

        public async Task WaitForStallAsync()
        {
            (await _stalled.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue("no attempt stalled");
        }

        /// <summary>Waits for the next stalled attempt, then advances the fake clock by the attempt timeout.</summary>
        public async Task FireTimeoutAsync(FakeTimeProvider time, TimeSpan timeout)
        {
            await WaitForStallAsync();
            time.Advance(timeout);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            attempt.ShouldBeLessThanOrEqualTo(StubHandler.RunawayLimit);
            lock (_keys)
            {
                _keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var values) ? string.Join(",", values) : null);
            }

            if (respond(attempt) is { } response)
            {
                return response;
            }

            _stalled.Release();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }

            throw new IOException("stalled attempt was never cancelled");
        }
    }
}
