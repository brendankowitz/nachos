using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.AI;
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
        using var client = new NachosFallbackChatClient(primary, [secondary]);

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
        using var client = new NachosFallbackChatClient(primary, [secondary]);

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetResponseAsync(Prompt, cancellationToken: CancellationToken.None));

        secondary.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChainExhausted_ThrowsLastError()
    {
        var primary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("first", null, HttpStatusCode.TooManyRequests));
        var secondary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("last", null, HttpStatusCode.BadGateway));
        using var client = new NachosFallbackChatClient(primary, [secondary]);

        var error = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetResponseAsync(Prompt, cancellationToken: CancellationToken.None));

        error.Message.ShouldBe("last");
    }

    [Fact]
    public async Task CallerCancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var primary = new ScriptedChatClient().EnqueueFailure(new TaskCanceledException());
        var secondary = new ScriptedChatClient().EnqueueText("unused");
        using var client = new NachosFallbackChatClient(primary, [secondary]);

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.GetResponseAsync(Prompt, cancellationToken: cts.Token));

        secondary.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Streaming_FailureBeforeFirstUpdate_UsesNextProfile()
    {
        var primary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests));
        var secondary = new ScriptedChatClient().EnqueueText("streamed");
        using var client = new NachosFallbackChatClient(primary, [secondary]);

        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync(Prompt, cancellationToken: CancellationToken.None))
        {
            text += update.Text;
        }

        text.ShouldBe("streamed");
    }

    private static PipelineResponse StatusResponse(int status)
    {
        var response = Substitute.For<PipelineResponse>();
        response.Status.Returns(status);
        return response;
    }
}
