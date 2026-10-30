using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Nachos.Core.Llm;
using Nachos.Testing;

namespace Nachos.Core.Tests.Llm;

public sealed class FallbackChatClientTests
{
    private static readonly ChatMessage[] Prompt = [new(ChatRole.User, "hello")];

    public static TheoryData<Exception> RetryableErrors() => new()
    {
        new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests),
        new HttpRequestException("timeout", null, HttpStatusCode.RequestTimeout),
        new HttpRequestException("down", null, HttpStatusCode.BadGateway),
        new HttpRequestException("connection reset"),
        new TimeoutException(),
        new InvalidOperationException("wrapper", new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable)),
        new ClientResultException(StatusResponse(429)),
    };

    [Theory]
    [MemberData(nameof(RetryableErrors))]
    public async Task RetryableError_UsesNextProfile(Exception error)
    {
        var primary = new ScriptedChatClient().EnqueueFailure(error);
        var secondary = new ScriptedChatClient().EnqueueText("from fallback");
        using var client = Create(primary, secondary);

        var response = await client.GetResponseAsync(Prompt, cancellationToken: CancellationToken.None);

        response.Text.ShouldBe("from fallback");
        primary.Requests.Count.ShouldBe(1);
        secondary.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task NonRetryable_Throws(HttpStatusCode status)
    {
        var primary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("bad", null, status));
        var secondary = new ScriptedChatClient().EnqueueText("unused");
        using var client = Create(primary, secondary);

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetResponseAsync(Prompt, cancellationToken: CancellationToken.None));

        secondary.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChainExhausted_ThrowsLastError()
    {
        var primary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("first", null, HttpStatusCode.TooManyRequests));
        var secondary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("last", null, HttpStatusCode.BadGateway));
        using var client = Create(primary, secondary);

        var error = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetResponseAsync(Prompt, cancellationToken: CancellationToken.None));

        error.Message.ShouldBe("last");
    }

    [Fact]
    public async Task CallerCancellationDuringTheCall_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var primary = new ScriptedChatClient().EnqueueFailure(() =>
        {
            cts.Cancel();
            return new TaskCanceledException("cancelled", null, cts.Token);
        });
        var secondary = new ScriptedChatClient().EnqueueText("unused");
        using var client = Create(primary, secondary);

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetResponseAsync(Prompt, cancellationToken: cts.Token));

        secondary.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task HttpClientTimeout_WithLiveCallerToken_UsesNextProfile()
    {
        var primary = new ScriptedChatClient().EnqueueFailure(
            new TaskCanceledException("timed out", new TimeoutException()));
        var secondary = new ScriptedChatClient().EnqueueText("from fallback");
        using var client = Create(primary, secondary);

        var response = await client.GetResponseAsync(Prompt, cancellationToken: CancellationToken.None);

        response.Text.ShouldBe("from fallback");
    }

    [Fact]
    public async Task Fallback_IsLoggedAndRecordedOnTheActivity()
    {
        var logger = new ListLogger();
        var primary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests));
        var secondary = new ScriptedChatClient().EnqueueText("from fallback");
        using var client = new NachosFallbackChatClient(primary, [secondary], ["fast", "big"], logger);
        using var source = new ActivitySource("fallback-test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "fallback-test",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("call");

        await client.GetResponseAsync(Prompt, cancellationToken: CancellationToken.None);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("fast");
        entry.Message.ShouldContain("big");
        entry.Message.ShouldContain("429");
        var fallbackEvent = activity!.Events.ShouldHaveSingleItem();
        fallbackEvent.Name.ShouldBe("nachos.llm.fallback");
    }

    [Fact]
    public async Task Streaming_FailureBeforeFirstUpdate_UsesNextProfile()
    {
        var primary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests));
        var secondary = new ScriptedChatClient().EnqueueText("streamed");
        using var client = Create(primary, secondary);

        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync(Prompt, cancellationToken: CancellationToken.None))
        {
            text += update.Text;
        }

        text.ShouldBe("streamed");
    }

    private static NachosFallbackChatClient Create(IChatClient primary, IChatClient secondary) =>
        new(primary, [secondary], ["primary", "secondary"], new ListLogger());

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static PipelineResponse StatusResponse(int status)
    {
        var response = Substitute.For<PipelineResponse>();
        response.Status.Returns(status);
        return response;
    }
}
