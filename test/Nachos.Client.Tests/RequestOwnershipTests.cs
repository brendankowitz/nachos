using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// Who disposes what. <see cref="RetryHandler"/> owns every per-attempt copy it creates and disposes each one before
/// <c>SendAsync</c> returns or throws; the response it returns references the caller's original request. The original
/// stays the caller's (<see cref="NachosHttpClient"/> disposes its own).
/// </summary>
public sealed class RequestOwnershipTests
{
    private const string MessagesTemplate = "/v3/workspaces/{workspace_id}/sessions/{session_id}/messages";

    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","metadata":{},"created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    private const int BufferLimit = 1024;

    private static readonly Uri Base = new("https://nachos.test/");

    private static readonly IReadOnlyList<MessageCreate> Batch = [new("hello", "alice")];

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Every return and throw path of the retry loop, through the high-level client. <c>create</c> is a keyed POST with
    /// a body, <c>get</c> a bodyless GET. Afterwards no request the handler sent may still be undisposed.
    /// </summary>
    [Theory]
    [InlineData("create", "success", 1, true)]
    [InlineData("get", "success", 1, true)]
    [InlineData("create", "ok-after-two-503", 3, true)]
    [InlineData("get", "ok-after-two-503", 3, true)]
    [InlineData("create", "exhausted-503", 3, false)]
    [InlineData("get", "exhausted-503", 3, false)]
    [InlineData("create", "429-retry-after-31s", 1, false)]
    [InlineData("get", "terminal-404", 1, false)]
    [InlineData("get", "terminal-501", 1, false)]
    [InlineData("create", "transport-exhausted", 3, false)]
    [InlineData("get", "send-throws-non-transient", 1, false)]
    [InlineData("create", "body-failure-exhausted", 3, false)]
    [InlineData("get", "body-failure-on-404", 1, false)]
    [InlineData("create", "body-failure-retry-after-120s", 1, false)]
    [InlineData("get", "over-buffer-limit", 1, false)]
    [InlineData("create", "cancel-during-send", 1, false)]
    [InlineData("create", "cancel-during-body", 1, false)]
    [InlineData("get", "cancel-during-backoff", 1, false)]
    public async Task EveryRequestCopy_IsDisposed_OnEveryPath(string op, string path, int sends, bool succeeds)
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHandler((_, attempt) => Respond(op, path, attempt, cts));
        var recorder = new OriginalRecorder { InnerHandler = Retry(stub, path == "cancel-during-backoff" ? cts : null) };
        var client = new NachosHttpClient(new HttpClient(recorder), new NachosClientOptions { BaseAddress = Base });

        Task call = op == "create"
            ? client.CreateMessagesAsync("w1", "s1", Batch, ct: cts.Token)
            : client.GetMessageAsync("w1", "s1", "m1", cts.Token);
        if (succeeds)
        {
            await call;
        }
        else
        {
            await Should.ThrowAsync<Exception>(() => call);
        }

        var original = recorder.Requests.Single();
        stub.Messages.Count.ShouldBe(sends);
        stub.Messages.ShouldAllBe(m => !ReferenceEquals(m, original), "the retry path sends copies, never the original");
        stub.Messages.Select(StubHandler.IsDisposed).ShouldAllBe(disposed => disposed, "every copy must be disposed");
        StubHandler.IsDisposed(original).ShouldBeTrue("NachosHttpClient disposes the request it created");
    }

    [Theory]
    [InlineData("success", 1)]
    [InlineData("ok-after-two-503", 3)]
    [InlineData("exhausted-503", 3)]
    [InlineData("429-retry-after-31s", 1)]
    [InlineData("terminal-501", 1)]
    public async Task ReturnedResponse_ReferencesTheCallersRequest_WhichStaysTheCallers(string path, int sends)
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHandler((_, attempt) => Respond("create", path, attempt, cts));
        using var http = new HttpClient(Retry(stub, cancelInBackoff: null));
        var content = new TrackedContent("""{"messages":[]}""");
        using var request = KeyedCreate(content);

        using var response = await http.SendAsync(request);

        response.RequestMessage.ShouldBeSameAs(request);
        StubHandler.IsDisposed(request).ShouldBeFalse("the caller owns its request");
        content.Disposed.ShouldBeFalse();
        stub.Messages.Count.ShouldBe(sends);
        stub.Messages.Select(StubHandler.IsDisposed).ShouldAllBe(disposed => disposed);
    }

    /// <summary>
    /// A redirect followed below the retry handler (as SocketsHttpHandler does) rewrites the URI of the request it
    /// was given, which is the per-attempt copy. The returned response references the caller's original request, so
    /// its <c>RequestUri</c> is the pre-redirect URI; the final URI is not carried back.
    /// </summary>
    [Fact]
    public async Task RedirectBelowTheHandler_ReturnedRequestMessage_KeepsThePreRedirectUri()
    {
        var redirected = new Uri(Base, "/v3/workspaces/w1/sessions/s1/messages/m2");
        var inner = new RedirectingHandler(redirected, MessageJson);
        using var http = new HttpClient(Retry(inner, cancelInBackoff: null));
        var originalUri = new Uri(Base, "/v3/workspaces/w1/sessions/s1/messages/m1");
        using var request = new HttpRequestMessage(HttpMethod.Get, originalUri);
        request.Options.Set(RetryHandler.RouteTemplate, "/v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}");

        using var response = await http.SendAsync(request);

        response.RequestMessage.ShouldBeSameAs(request);
        response.RequestMessage!.RequestUri.ShouldBe(originalUri);
        inner.Seen.ShouldNotBeSameAs(request);
        inner.Seen!.RequestUri.ShouldBe(redirected);
        StubHandler.IsDisposed(inner.Seen).ShouldBeTrue();
    }

    [Fact]
    public async Task NonRetryableRequest_PassesThroughUncopied_AndIsNotDisposedByTheHandler()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        using var http = new HttpClient(Retry(stub, cancelInBackoff: null));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, "/v3/workspaces/w1/sessions/s1/messages"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Options.Set(RetryHandler.RouteTemplate, MessagesTemplate);

        using var response = await http.SendAsync(request);

        stub.Messages.Single().ShouldBeSameAs(request);
        StubHandler.IsDisposed(request).ShouldBeFalse();
    }

    private static HttpRequestMessage KeyedCreate(HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, "/v3/workspaces/w1/sessions/s1/messages")) { Content = content };
        request.Options.Set(RetryHandler.RouteTemplate, MessagesTemplate);
        request.Headers.Add("Idempotency-Key", "k1");
        return request;
    }

    private static HttpResponseMessage Respond(string op, string path, int attempt, CancellationTokenSource cts)
    {
        HttpResponseMessage Ok() => op == "create"
            ? StubHandler.Json(HttpStatusCode.Created, "[]")
            : StubHandler.Json(HttpStatusCode.OK, MessageJson);
        HttpResponseMessage Error(HttpStatusCode status) => StubHandler.Json(status, """{"detail":"x"}""");

        switch (path)
        {
            case "success":
                return Ok();
            case "ok-after-two-503":
                return attempt < 3 ? Error(HttpStatusCode.ServiceUnavailable) : Ok();
            case "exhausted-503":
            case "cancel-during-backoff":
                return Error(HttpStatusCode.ServiceUnavailable);
            case "429-retry-after-31s":
                var limited = Error(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(31));
                return limited;
            case "terminal-404":
                return Error(HttpStatusCode.NotFound);
            case "terminal-501":
                return Error(HttpStatusCode.NotImplemented);
            case "transport-exhausted":
                throw new HttpRequestException("connection reset");
            case "send-throws-non-transient":
                throw new InvalidOperationException("a bug below the retry handler");
            case "body-failure-exhausted":
                return StubHandler.BrokenBody(op == "create" ? HttpStatusCode.Created : HttpStatusCode.OK, new BrokenStream());
            case "body-failure-on-404":
                return StubHandler.BrokenBody(HttpStatusCode.NotFound, new BrokenStream());
            case "body-failure-retry-after-120s":
                var broken = StubHandler.BrokenBody(HttpStatusCode.ServiceUnavailable, new BrokenStream());
                broken.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return broken;
            case "over-buffer-limit":
                return StubHandler.Json(HttpStatusCode.OK, MessageJson.PadRight(BufferLimit + 1));
            case "cancel-during-send":
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            case "cancel-during-body":
                var content = new StreamContent(new StallingStream(cts.Cancel));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = content };
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    /// <summary>
    /// A retry handler whose backoff never sleeps. With <paramref name="cancelInBackoff"/>, the first wait cancels it,
    /// standing in for a caller that gives up mid-backoff.
    /// </summary>
    private RetryHandler Retry(HttpMessageHandler inner, CancellationTokenSource? cancelInBackoff) =>
        new(
            _time,
            async (_, ct) =>
            {
                if (cancelInBackoff is not null)
                {
                    await cancelInBackoff.CancelAsync();
                }

                ct.ThrowIfCancellationRequested();
            },
            () => 0.5,
            BufferLimit)
        {
            InnerHandler = inner,
        };

    /// <summary>Follows a redirect the way SocketsHttpHandler does: it rewrites the URI of the request it was given.</summary>
    private sealed class RedirectingHandler(Uri target, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            request.RequestUri = target;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    /// <summary>Records the request as the caller handed it to the pipeline, before any retry copy is made.</summary>
    private sealed class OriginalRecorder : DelegatingHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
