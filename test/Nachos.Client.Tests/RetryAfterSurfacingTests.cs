using System.Net;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// Spec §16: a <c>Retry-After</c> longer than 30 s is not waited out; the error reaches the caller with the requested
/// delay. A status response mapped by the client, or a body failure wrapped by the retry handler, that carried a
/// parseable <c>Retry-After</c> holds the delay in <see cref="Exception.Data"/> under
/// <see cref="NachosExceptionData.RetryAfter"/> and ends its message with <c>" Retry-After: {N}s."</c>. A delay of
/// exactly 30 s is still waited out. Not covered: a body that fails inside <see cref="HttpClient"/> buffering on a
/// never-retried route, a malformed 2xx body, and a timeout below the handler.
/// </summary>
public sealed class RetryAfterSurfacingTests
{
    private const string Key = NachosExceptionData.RetryAfter;

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly Uri Base = new("https://nachos.test/");

    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","metadata":{},"created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    private static readonly IReadOnlyList<MessageCreate> Batch = [new("hello", "alice")];

    private readonly FakeTimeProvider _time = new(Now);

    private readonly List<TimeSpan> _delays = [];

    [Fact]
    public async Task Status429_RetryAfter31Seconds_SurfacesWithTheDelay_WithoutWaiting()
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(Error(HttpStatusCode.TooManyRequests), "31"));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(31));
        ex.Message.ShouldEndWith(" Retry-After: 31s.");
        stub.Requests.Count.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Status_RetryAfterExactlyTheCap_IsWaitedOnce_ThenSucceeds(HttpStatusCode status)
    {
        var stub = new StubHandler((_, attempt) => attempt == 1
            ? WithRetryAfter(Error(status), "30")
            : StubHandler.Json(HttpStatusCode.OK, MessageJson));

        var message = await Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1");

        message.Id.ShouldBe("m1");
        stub.Requests.Count.ShouldBe(2);
        _delays.ShouldBe([TimeSpan.FromSeconds(30)]);
    }

    [Fact]
    public async Task BodyFailureAfter2xx_RetryAfterExactlyTheCap_IsWaitedOnce_ThenSucceeds()
    {
        var stub = new StubHandler((_, attempt) => attempt == 1
            ? WithRetryAfter(StubHandler.BrokenBody(HttpStatusCode.Created, new BrokenStream()), "30")
            : StubHandler.Json(HttpStatusCode.Created, "[]"));

        await Client(Retry(stub)).CreateMessagesAsync("w1", "s1", Batch, "k1");

        stub.Requests.Count.ShouldBe(2);
        stub.Requests[1].IdempotencyKey.ShouldBe("k1");
        _delays.ShouldBe([TimeSpan.FromSeconds(30)]);
    }

    [Fact]
    public async Task Status429_RetryAfterHttpDate_SurfacesTheDelayFromTheClock()
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(Error(HttpStatusCode.TooManyRequests), Now.AddSeconds(40).ToString("R")));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(40));
        ex.Message.ShouldEndWith(" Retry-After: 40s.");
        stub.Requests.Count.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task FractionalDelay_IsRoundedUpInTheMessage_AndExactInData()
    {
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var stub = new StubHandler((_, _) => WithRetryAfter(Error(HttpStatusCode.TooManyRequests), Now.AddSeconds(40).ToString("R")));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(39.5));
        ex.Message.ShouldEndWith(" Retry-After: 40s.");
    }

    [Fact]
    public async Task HttpDateInThePast_IsAZeroDelay()
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(Error(HttpStatusCode.NotFound), Now.AddSeconds(-30).ToString("R")));

        var ex = await Should.ThrowAsync<NotFoundException>(() => Client(stub).GetMessageAsync("w1", "s1", "m1"));

        ex.Data[Key].ShouldBe(TimeSpan.Zero);
        ex.Message.ShouldEndWith(" Retry-After: 0s.");
    }

    [Fact]
    public async Task Status503_RetryAfter120_OnAKeyedCreate_SurfacesWithTheDelay()
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(Error(HttpStatusCode.ServiceUnavailable), "120"));

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => Client(Retry(stub)).CreateMessagesAsync("w1", "s1", Batch, "k1"));

        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(120));
        ex.Message.ShouldEndWith(" Retry-After: 120s.");
        stub.Requests.Count.ShouldBe(1);
        stub.Requests[0].IdempotencyKey.ShouldBe("k1");
        _delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task BodyFailureAfter2xx_WithRetryAfter45_SurfacesWithTheDelay()
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(StubHandler.BrokenBody(HttpStatusCode.Created, new BrokenStream()), "45"));

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => Client(Retry(stub)).CreateMessagesAsync("w1", "s1", Batch, "k1"));

        ex.StatusCode.ShouldBe(HttpStatusCode.Created);
        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(45));
        ex.Message.ShouldEndWith(" Retry-After: 45s.");
        ex.InnerException.ShouldBeNull();
        stub.Requests.Count.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task BodyFailureOnAFinalStatus_WithRetryAfter_SurfacesWithTheDelay()
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(StubHandler.BrokenBody(HttpStatusCode.NotFound, new BrokenStream()), "3"));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(3));
        ex.Message.ShouldEndWith(" Retry-After: 3s.");
        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExhaustedStatusRetries_CarryTheLastResponsesDelay()
    {
        var stub = new StubHandler((_, attempt) => WithRetryAfter(Error(HttpStatusCode.ServiceUnavailable), attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        stub.Requests.Count.ShouldBe(3);
        _delays.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);
        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(3));
        ex.Message.ShouldEndWith(" Retry-After: 3s.");
    }

    [Fact]
    public async Task ExhaustedBodyFailures_CarryTheLastResponsesDelay()
    {
        var stub = new StubHandler((_, attempt) => WithRetryAfter(
            StubHandler.BrokenBody(HttpStatusCode.ServiceUnavailable, new BrokenStream()),
            attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));

        stub.Requests.Count.ShouldBe(3);
        _delays.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);
        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(3));
        ex.Message.ShouldEndWith(" Retry-After: 3s.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("")]
    public async Task AbsentOrUnparseableRetryAfter_AddsNoDataAndNoSuffix(string? header)
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(Error(HttpStatusCode.ServiceUnavailable), header));
        var brokenStub = new StubHandler((_, _) => WithRetryAfter(StubHandler.BrokenBody(HttpStatusCode.NotFound, new BrokenStream()), header));

        var status = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(stub)).GetMessageAsync("w1", "s1", "m1"));
        var body = await Should.ThrowAsync<HttpRequestException>(() => Client(Retry(brokenStub)).GetMessageAsync("w1", "s1", "m1"));

        foreach (var ex in new[] { status, body })
        {
            ex.Data.Contains(Key).ShouldBeFalse();
            ex.Message.ShouldNotContain("Retry-After");
        }
    }

    /// <summary>Every mapped status exception carries the delay, not only retryable ones or delays over the cap.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"detail":"d"}""", typeof(AuthException))]
    [InlineData(HttpStatusCode.Forbidden, """{"detail":"d"}""", typeof(AuthException))]
    [InlineData(HttpStatusCode.NotFound, """{"detail":"d"}""", typeof(NotFoundException))]
    [InlineData(HttpStatusCode.Conflict, """{"detail":"d"}""", typeof(ConflictException))]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"detail":"d"}""", typeof(NachosValidationException))]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"detail":"d","type":"urn:nachos:problem:idempotency-key-reused"}""", typeof(IdempotencyKeyReusedException))]
    [InlineData(HttpStatusCode.BadRequest, """{"detail":"d"}""", typeof(HttpRequestException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<html>", typeof(HttpRequestException))]
    public async Task EveryMappedException_CarriesTheDelay(HttpStatusCode status, string body, Type expected)
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(StubHandler.Json(status, body), "5"));

        var ex = await Should.ThrowAsync<Exception>(() => Client(stub).GetMessageAsync("w1", "s1", "m1"));

        ex.GetType().ShouldBe(expected);
        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(5));
        ex.Message.ShouldEndWith(" Retry-After: 5s.");
    }

    [Fact]
    public async Task RequestValidationException_CarriesTheDelayInDataOnly()
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(
            StubHandler.Json(HttpStatusCode.UnprocessableEntity, """{"detail":[{"loc":["body"],"msg":"m","type":"t"}]}"""), "5"));

        var ex = await Should.ThrowAsync<RequestValidationException>(() => Client(stub).GetMessageAsync("w1", "s1", "m1"));

        ex.Data[Key].ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Suffix_SurvivesTruncation_AndTheMessageStaysBounded()
    {
        var stub = new StubHandler((_, _) => WithRetryAfter(
            StubHandler.Json(HttpStatusCode.NotFound, $$"""{"detail":"{{new string('x', 100_000)}}"}"""), "2147483647"));

        var ex = await Should.ThrowAsync<NotFoundException>(() => Client(stub).GetMessageAsync("w1", "s1", "m1"));

        ex.Message.Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
        ex.Message.ShouldEndWith(ErrorMapper.TruncationMarker + " Retry-After: 2147483647s.");
    }

    private NachosHttpClient Client(HttpMessageHandler pipeline) =>
        new(new HttpClient(pipeline), new NachosClientOptions { BaseAddress = Base }, _time);

    private RetryHandler Retry(HttpMessageHandler inner) =>
        new(_time, RecordDelay, () => 0.5) { InnerHandler = inner };

    private static HttpResponseMessage Error(HttpStatusCode status) =>
        StubHandler.Json(status, $$"""{"detail":"status {{(int)status}}"}""");

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage response, string? value)
    {
        if (value is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", value);
        }

        return response;
    }

    private Task RecordDelay(TimeSpan delay, CancellationToken ct)
    {
        _delays.Add(delay);
        return Task.CompletedTask;
    }
}
