using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// The retry boundary, exercised end to end through <see cref="NachosHttpClient"/> and <see cref="RetryHandler"/>
/// against an in-test fake server. Delays go through the injected seam and are recorded, never slept.
/// </summary>
public sealed class RetryBoundaryTests
{
    private const string MessagesTemplate = "/v3/workspaces/{workspace_id}/sessions/{session_id}/messages";

    private static readonly Uri Base = new("https://nachos.test/");

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<MessageCreate> Batch =
    [
        new("hello", "alice", new JsonObject { ["k"] = "v" }),
        new("world", "bob"),
    ];

    private readonly FakeTimeProvider _time = new(Now);

    private readonly List<TimeSpan> _delays = [];

    [Fact]
    public async Task CommittedThenTransportFailure_RetriesWithKey_ExactlyOneBatch()
    {
        var server = new MessageServer(failAfterCommitOnAttempt: 1);
        var stub = new StubHandler(server.Handle);

        var messages = await Client(Retry(stub)).CreateMessagesAsync("w1", "s1", Batch);

        stub.Requests.Count.ShouldBe(2);
        server.Batches.ShouldBe(1);
        var keys = stub.Requests.Select(r => r.IdempotencyKey).ToArray();
        keys.ShouldAllBe(k => IsGuid(k));
        keys.Distinct().Count().ShouldBe(1);
        messages.Select(m => m.Content).ShouldBe(["hello", "world"]);
        messages.Select(m => m.Id).ShouldBe(["b1-0", "b1-1"]);
    }

    [Fact]
    public async Task WithoutKey_NoReplay()
    {
        var server = new MessageServer(failAfterCommitOnAttempt: 1);
        var stub = new StubHandler(server.Handle);
        var pipeline = new StripIdempotencyKey { InnerHandler = Retry(stub) };

        await Should.ThrowAsync<HttpRequestException>(() => Client(pipeline).CreateMessagesAsync("w1", "s1", Batch));

        stub.Requests.Count.ShouldBe(1);
        stub.Requests[0].IdempotencyKey.ShouldBeNull();
        server.Batches.ShouldBe(1);
    }

    [Fact]
    public async Task Honors_RetryAfter_DeltaSeconds()
    {
        var stub = new StubHandler((_, attempt) => attempt == 1
            ? WithRetryAfter(Error(HttpStatusCode.TooManyRequests), new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)))
            : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        var message = await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        message.Id.ShouldBe("m1");
        stub.Requests.Count.ShouldBe(2);
        _delays.ShouldBe([TimeSpan.FromSeconds(7)]);
    }

    [Fact]
    public async Task Honors_RetryAfter_HttpDate()
    {
        var stub = new StubHandler((_, attempt) => attempt == 1
            ? WithRetryAfter(Error(HttpStatusCode.ServiceUnavailable), new RetryConditionHeaderValue(Now.AddSeconds(12)))
            : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        stub.Requests.Count.ShouldBe(2);
        _delays.ShouldBe([TimeSpan.FromSeconds(12)]);
    }

    [Fact]
    public async Task RetryAfter_HttpDateInThePast_RetriesWithoutWaiting()
    {
        var stub = new StubHandler((_, attempt) => attempt == 1
            ? WithRetryAfter(Error(HttpStatusCode.TooManyRequests), new RetryConditionHeaderValue(Now.AddSeconds(-30)))
            : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        _delays.ShouldBe([TimeSpan.Zero]);
    }

    [Fact]
    public async Task RetryAfter_BeyondCap_SurfacesTheResponseWithoutRetrying()
    {
        var stub = new StubHandler((_, _) =>
            WithRetryAfter(Error(HttpStatusCode.TooManyRequests), new RetryConditionHeaderValue(TimeSpan.FromHours(1))));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        stub.Requests.Count.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task TransientStatus_ThenOk_Succeeds(HttpStatusCode first)
    {
        var stub = new StubHandler((_, attempt) => attempt == 1 ? Error(first) : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        var message = await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        message.Id.ShouldBe("m1");
        stub.Requests.Count.ShouldBe(2);
        _delays.ShouldBe([TimeSpan.FromMilliseconds(250)]);
    }

    [Fact]
    public async Task ThreeAttemptsMax_LastStatusSurfaces_WithExponentialJitteredBackoff()
    {
        var stub = new StubHandler((_, _) => Error(HttpStatusCode.ServiceUnavailable));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stub.Requests.Count.ShouldBe(RetryHandler.MaxAttempts);
        stub.Requests.Count.ShouldBe(3);
        _delays.ShouldBe([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)]);
    }

    [Fact]
    public async Task FullJitter_ScalesTheExponentialCeiling()
    {
        var stub = new StubHandler((_, _) => Error(HttpStatusCode.ServiceUnavailable));

        await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub, jitter: () => 0.0)).GetMessageAsync("w1", "s1", "m1"));
        _delays.ShouldBe([TimeSpan.Zero, TimeSpan.Zero]);

        _delays.Clear();
        await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub, jitter: () => 0.999)).GetMessageAsync("w1", "s1", "m1"));
        _delays[0].ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
        _delays[1].ShouldBeLessThan(TimeSpan.FromSeconds(1));
        _delays[1].ShouldBeGreaterThan(_delays[0]);
    }

    [Fact]
    public async Task ThreeAttemptsMax_LastTransportFailureSurfaces()
    {
        var stub = new StubHandler((_, attempt) => throw new HttpRequestException($"connection reset on attempt {attempt}"));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        ex.Message.ShouldBe("connection reset on attempt 3");
        stub.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task UnwrappedIOException_FromTheInnerHandler_IsRetried()
    {
        var stub = new StubHandler((_, attempt) => attempt == 1
            ? throw new IOException("connection reset (unwrapped)")
            : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        stub.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TimeoutStyleFailure_NotCallerCancellation_IsRetried()
    {
        var stub = new StubHandler((_, attempt) => attempt == 1
            ? throw new TaskCanceledException("attempt timed out", new TimeoutException())
            : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        stub.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("GET", "/v3/workspaces/w1/webhooks/test", "/v3/workspaces/{workspace_id}/webhooks/test", false)]
    [InlineData("POST", "/v3/workspaces/w1/chat", "/v3/workspaces/{workspace_id}/chat", true)]
    [InlineData("POST", "/v3/workspaces/w1/peers/p1/chat", "/v3/workspaces/{workspace_id}/peers/{peer_id}/chat", true)]
    [InlineData("POST", "/v3/keys", "/v3/keys", false)]
    public async Task NeverRetryableRoute_IsSentOnce(string method, string path, string template, bool withKey)
    {
        var stub = new StubHandler((_, _) => Error(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(Retry(stub));
        using var request = Raw(new HttpMethod(method), path, template);
        if (withKey)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        using var response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RequestWithoutRouteTemplate_IsSentOnce()
    {
        var stub = new StubHandler((_, _) => Error(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(Retry(stub));
        using var request = Raw(HttpMethod.Get, "/v3/workspaces/w1/sessions/s1/messages/m1", template: null);

        using var response = await http.SendAsync(request);

        stub.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task NonTransientStatus_IsNotRetried(HttpStatusCode status)
    {
        var stub = new StubHandler((_, _) => Error(status));

        await Should.ThrowAsync<Exception>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RequestBody_AndKey_AreIdenticalOnEveryAttempt()
    {
        var stub = new StubHandler((r, attempt) => attempt < 3
            ? Error(HttpStatusCode.ServiceUnavailable)
            : StubHandler.Json(HttpStatusCode.Created, "[]"));

        await Client(Retry(stub)).CreateMessagesAsync("w1", "s1", Batch);

        stub.Requests.Count.ShouldBe(3);
        stub.Requests.Select(r => r.Body).Distinct().Count().ShouldBe(1);
        stub.Requests.Select(r => r.IdempotencyKey).Distinct().Count().ShouldBe(1);
        stub.Requests.ShouldAllBe(r => r.ContentTypeHeader == "application/json; charset=utf-8");
        JsonNode.DeepEquals(
            JsonNode.Parse(stub.Requests[0].Body!),
            JsonNode.Parse("""{"messages":[{"content":"hello","peer_id":"alice","metadata":{"k":"v"}},{"content":"world","peer_id":"bob"}]}"""))
            .ShouldBeTrue(stub.Requests[0].Body);
    }

    [Fact]
    public async Task CommittedThenBodyReadFailure_RetriesWithKey_ExactlyOneBatch()
    {
        var server = new MessageServer(failAfterCommitOnAttempt: 1, brokenBody: true);
        var stub = new StubHandler(server.Handle);

        var messages = await Client(Retry(stub)).CreateMessagesAsync("w1", "s1", Batch);

        stub.Requests.Count.ShouldBe(2);
        server.Batches.ShouldBe(1);
        stub.Requests.Select(r => r.IdempotencyKey).Distinct().Count().ShouldBe(1);
        IsGuid(stub.Requests[0].IdempotencyKey).ShouldBeTrue();
        messages.Select(m => m.Id).ShouldBe(["b1-0", "b1-1"]);
        server.BrokenBodies.Single().Disposed.ShouldBeTrue("the failed attempt's response must be disposed");
    }

    [Fact]
    public async Task BodyReadFailure_WithoutKey_NoReplay()
    {
        var server = new MessageServer(failAfterCommitOnAttempt: 1, brokenBody: true);
        var stub = new StubHandler(server.Handle);
        var pipeline = new StripIdempotencyKey { InnerHandler = Retry(stub) };

        await Should.ThrowAsync<HttpRequestException>(() => Client(pipeline).CreateMessagesAsync("w1", "s1", Batch));

        stub.Requests.Count.ShouldBe(1);
        stub.Requests[0].IdempotencyKey.ShouldBeNull();
        server.Batches.ShouldBe(1);
    }

    [Fact]
    public async Task BodyReadFailure_OnNonRetryableRoute_IsNotRetried()
    {
        var stub = new StubHandler((_, _) => StubHandler.BrokenBody(HttpStatusCode.OK, new BrokenStream()));
        using var http = new HttpClient(Retry(stub));
        using var request = Raw(HttpMethod.Post, "/v3/workspaces/w1/chat", "/v3/workspaces/{workspace_id}/chat");
        request.Content = new StringContent("""{"query":"hi"}""", System.Text.Encoding.UTF8, "application/json");

        await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(request));

        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FailedAttemptResponses_AreDisposedBeforeTheNextAttempt()
    {
        var contents = new List<TrackedContent>();
        var disposedBeforeNext = new List<bool>();
        var stub = new StubHandler((_, attempt) =>
        {
            disposedBeforeNext.AddRange(contents.Select(c => c.Disposed));
            var content = new TrackedContent(attempt < 3 ? """{"detail":"busy"}""" : MessageJson);
            contents.Add(content);
            return new HttpResponseMessage(attempt < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = content };
        });

        await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        stub.Requests.Count.ShouldBe(3);
        disposedBeforeNext.ShouldBe([true, true, true]);
    }

    [Fact]
    public async Task EachLogicalCreateMessagesCall_GetsItsOwnKey()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.Created, "[]"));
        var client = Client(Retry(stub));

        await client.CreateMessagesAsync("w1", "s1", Batch);
        await client.CreateMessagesAsync("w1", "s1", Batch);

        stub.Requests.Select(r => r.IdempotencyKey).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task CancellationDuringBackoff_StopsImmediately()
    {
        using var cts = new CancellationTokenSource();
        var backoffEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new StubHandler((_, _) => Error(HttpStatusCode.ServiceUnavailable));
        var handler = new RetryHandler(
            _time,
            async (_, ct) =>
            {
                backoffEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            () => 0.5)
        {
            InnerHandler = stub,
        };

        var call = Client(handler).GetMessageAsync("w1", "s1", "m1", cts.Token);
        await backoffEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CallerCancellation_AfterATransientStatus_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHandler((_, _) =>
        {
            cts.Cancel();
            return Error(HttpStatusCode.ServiceUnavailable);
        });

        await Should.ThrowAsync<OperationCanceledException>(
            () => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1", cts.Token));

        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CallerCancellation_DuringATransportFailure_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHandler((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Should.ThrowAsync<OperationCanceledException>(
            () => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1", cts.Token));

        stub.Requests.Count.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellation_WithAConcurrentTransportError_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHandler((_, _) =>
        {
            cts.Cancel();
            throw new HttpRequestException("socket closed while cancelling");
        });

        // HttpClient reports any failure after the caller cancelled as a cancellation.
        await Should.ThrowAsync<OperationCanceledException>(
            () => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1", cts.Token));

        stub.Requests.Count.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","metadata":{},"created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    private static bool IsGuid(string? value) => Guid.TryParse(value, out _);

    private static NachosHttpClient Client(HttpMessageHandler pipeline) =>
        new NachosHttpClient(new HttpClient(pipeline), new NachosClientOptions { BaseAddress = Base, ApiKey = "test-key" });

    private static HttpRequestMessage Raw(HttpMethod method, string path, string? template)
    {
        var request = new HttpRequestMessage(method, new Uri(Base, path));
        if (template is not null)
        {
            request.Options.Set(RetryHandler.RouteTemplate, template);
        }

        return request;
    }

    private static HttpResponseMessage Error(HttpStatusCode status) =>
        StubHandler.Json(status, $$"""{"detail":"status {{(int)status}}"}""");

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage response, RetryConditionHeaderValue value)
    {
        response.Headers.RetryAfter = value;
        return response;
    }

    private RetryHandler Retry(HttpMessageHandler inner, Func<double>? jitter = null) =>
        new(_time, RecordDelay, jitter ?? (() => 0.5)) { InnerHandler = inner };

    private Task RecordDelay(TimeSpan delay, CancellationToken ct)
    {
        lock (_delays)
        {
            _delays.Add(delay);
        }

        return Task.CompletedTask;
    }

    /// <summary>Removes the Idempotency-Key before the retry handler sees the request (a "raw" caller).</summary>
    private sealed class StripIdempotencyKey : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("Idempotency-Key");
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A message endpoint with Idempotency-Key replay: a keyed request whose key was seen replays the stored
    /// response; otherwise the batch commits. It can drop the response after committing on a chosen attempt.
    /// </summary>
    private sealed class MessageServer(int failAfterCommitOnAttempt, bool brokenBody = false)
    {
        public List<BrokenStream> BrokenBodies { get; } = [];

        private readonly Dictionary<string, string> _replays = new(StringComparer.Ordinal);

        public int Batches { get; private set; }

        public HttpResponseMessage Handle(RecordedRequest request, int attempt)
        {
            request.RouteTemplate.ShouldBe(MessagesTemplate);
            if (request.IdempotencyKey is { } seen && _replays.TryGetValue(seen, out var stored))
            {
                return StubHandler.Json(HttpStatusCode.Created, stored);
            }

            Batches++;
            var created = new JsonArray();
            var index = 0;
            foreach (var message in JsonNode.Parse(request.Body!)!["messages"]!.AsArray())
            {
                created.Add(new JsonObject
                {
                    ["id"] = $"b{Batches}-{index++}",
                    ["content"] = message!["content"]!.GetValue<string>(),
                    ["peer_id"] = message["peer_id"]!.GetValue<string>(),
                    ["session_id"] = "s1",
                    ["metadata"] = message["metadata"]?.DeepClone() ?? new JsonObject(),
                    ["created_at"] = "2026-10-08T12:00:00Z",
                    ["workspace_id"] = "w1",
                    ["token_count"] = 1,
                });
            }

            var body = created.ToJsonString();
            if (request.IdempotencyKey is { } key)
            {
                _replays[key] = body;
            }

            if (attempt == failAfterCommitOnAttempt)
            {
                if (!brokenBody)
                {
                    throw new HttpRequestException("connection reset after the batch committed");
                }

                var stream = new BrokenStream();
                BrokenBodies.Add(stream);
                return StubHandler.BrokenBody(HttpStatusCode.Created, stream);
            }

            return StubHandler.Json(HttpStatusCode.Created, body);
        }
    }
}
