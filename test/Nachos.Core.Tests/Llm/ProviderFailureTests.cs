using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Nachos.Core.Llm;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests.Llm;

public sealed class ProviderFailureTests
{
    public static TheoryData<string, Exception, bool> Cases => new()
    {
        { "http 503", new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable), true },
        { "http 429", new HttpRequestException("throttled", null, HttpStatusCode.TooManyRequests), true },
        { "http 408", new HttpRequestException("slow", null, HttpStatusCode.RequestTimeout), true },
        { "http without a status (connection)", new HttpRequestException("refused"), true },
        { "http 400", new HttpRequestException("bad", null, HttpStatusCode.BadRequest), false },
        { "http 401", new HttpRequestException("auth", null, HttpStatusCode.Unauthorized), false },
        { "http 404", new HttpRequestException("no deployment", null, HttpStatusCode.NotFound), false },
        { "client result 500", new ClientResultException(StatusResponse(500)), true },
        { "client result 404", new ClientResultException(StatusResponse(404)), false },
        { "timeout", new TimeoutException(), true },
        { "provider timeout", new TaskCanceledException("timed out"), true },
        { "wrapped 503", new InvalidOperationException("wrapper", new HttpRequestException("down", null, HttpStatusCode.BadGateway)), true },
        { "unrelated", new InvalidOperationException("bad answer"), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsTransient_ClassifiesProviderFailures(string name, Exception exception, bool transient)
    {
        ProviderFailure.IsTransient(exception, CancellationToken.None).ShouldBe(transient, name);
    }

    [Fact]
    public void ACancellationByTheCaller_IsNotTransient()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        ProviderFailure.IsTransient(new TaskCanceledException(), cancelled.Token).ShouldBeFalse();
    }

    private static PipelineResponse StatusResponse(int status)
    {
        var response = Substitute.For<PipelineResponse>();
        response.Status.Returns(status);
        return response;
    }
}
