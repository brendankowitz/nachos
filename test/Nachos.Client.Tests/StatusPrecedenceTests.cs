using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// Spec §16 status precedence: once a status line is received, that status decides. A non-retryable status
/// (<c>4xx</c> other than 408/429, <c>3xx</c>, and 501) is final even if reading its body then fails. A body failure
/// after a <c>2xx</c> or a retryable status (408, 429, <c>5xx</c> other than 501) is retried, and only for a request the
/// route rules allow to be replayed (here: the keyed message create; without the key it is sent once).
/// </summary>
public sealed class StatusPrecedenceTests
{
    private const string MessagesTemplate = "/v3/workspaces/{workspace_id}/sessions/{session_id}/messages";

    private static readonly Uri Base = new("https://nachos.test/");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    private readonly List<TimeSpan> _delays = [];

    /// <summary>
    /// Sends per call when the server answers <paramref name="status"/> on every attempt, with the body readable or
    /// failing mid-read, for the keyed (replayable) and unkeyed (never replayed) message create.
    /// </summary>
    [Theory]
    //          status  keyed:readable  keyed:broken  unkeyed:readable  unkeyed:broken
    [InlineData(200, 1, 3, 1, 1)]
    [InlineData(201, 1, 3, 1, 1)]
    [InlineData(204, 1, 3, 1, 1)]
    [InlineData(302, 1, 1, 1, 1)]
    [InlineData(400, 1, 1, 1, 1)]
    [InlineData(401, 1, 1, 1, 1)]
    [InlineData(403, 1, 1, 1, 1)]
    [InlineData(404, 1, 1, 1, 1)]
    [InlineData(408, 3, 3, 1, 1)]
    [InlineData(409, 1, 1, 1, 1)]
    [InlineData(422, 1, 1, 1, 1)]
    [InlineData(425, 1, 1, 1, 1)]
    [InlineData(429, 3, 3, 1, 1)]
    [InlineData(500, 3, 3, 1, 1)]
    [InlineData(501, 1, 1, 1, 1)]
    [InlineData(502, 3, 3, 1, 1)]
    [InlineData(503, 3, 3, 1, 1)]
    [InlineData(504, 3, 3, 1, 1)]
    public async Task Attempts_ByStatus_Key_AndBody(
        int status, int keyedReadable, int keyedBroken, int unkeyedReadable, int unkeyedBroken)
    {
        (await Attempts((HttpStatusCode)status, keyed: true, broken: false)).ShouldBe(keyedReadable, "keyed, readable body");
        (await Attempts((HttpStatusCode)status, keyed: true, broken: true)).ShouldBe(keyedBroken, "keyed, broken body");
        (await Attempts((HttpStatusCode)status, keyed: false, broken: false)).ShouldBe(unkeyedReadable, "unkeyed, readable body");
        (await Attempts((HttpStatusCode)status, keyed: false, broken: true)).ShouldBe(unkeyedBroken, "unkeyed, broken body");
    }

    /// <summary>A final body failure surfaces as the body-read exception, carrying the status that was received.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData((HttpStatusCode)425)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    public async Task SurfacedBodyFailure_CarriesTheReceivedStatus(HttpStatusCode status)
    {
        var stub = new StubHandler((_, _) => StubHandler.BrokenBody(status, new BrokenStream()));
        using var http = new HttpClient(Retry(stub));
        using var request = Create(keyed: true);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(request));

        ex.StatusCode.ShouldBe(status);

        // The read failure's own text can repeat server bytes, so it is withheld: fixed text, no inner exception.
        ex.Message.ShouldStartWith("The Nachos HTTP exchange failed (");
        ex.InnerException.ShouldBeNull();
    }

    /// <summary>Cedar's probe: a broken body on a permanent status used to cost three sends through the client.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task BrokenBody_OnPermanentStatus_ThroughTheClient_IsSentOnce(HttpStatusCode status)
    {
        var stub = new StubHandler((_, _) => StubHandler.BrokenBody(status, new BrokenStream()));
        var client = new NachosHttpClient(new HttpClient(Retry(stub)), new NachosClientOptions { BaseAddress = Base });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.GetMessageAsync("w1", "s1", "m1"));

        ex.StatusCode.ShouldBe(status);
        stub.Requests.Count.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    private async Task<int> Attempts(HttpStatusCode status, bool keyed, bool broken)
    {
        var stub = new StubHandler((_, _) => broken
            ? StubHandler.BrokenBody(status, new BrokenStream())
            : StubHandler.Json(status, """{"detail":"x"}"""));
        using var http = new HttpClient(Retry(stub));
        using var request = Create(keyed);

        try
        {
            using var response = await http.SendAsync(request);
            response.StatusCode.ShouldBe(status);
        }
        catch (HttpRequestException) when (broken)
        {
        }

        return stub.Requests.Count;
    }

    private static HttpRequestMessage Create(bool keyed)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, "/v3/workspaces/w1/sessions/s1/messages"))
        {
            Content = new StringContent("""{"messages":[]}""", Encoding.UTF8, "application/json"),
        };
        request.Options.Set(RetryHandler.RouteTemplate, MessagesTemplate);
        if (keyed)
        {
            request.Headers.Add("Idempotency-Key", "k1");
        }

        return request;
    }

    private RetryHandler Retry(HttpMessageHandler inner) =>
        new(_time, RecordDelay, () => 0.5) { InnerHandler = inner };

    private Task RecordDelay(TimeSpan delay, CancellationToken ct)
    {
        _delays.Add(delay);
        return Task.CompletedTask;
    }
}
