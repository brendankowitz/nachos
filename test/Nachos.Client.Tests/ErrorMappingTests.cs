using System.Net;
using System.Text;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>Server error shapes (spec §9: string or array <c>detail</c>, plus RFC 9457 members) to exceptions.</summary>
public sealed class ErrorMappingTests
{
    private const string ApiKey = "nk-SECRET-a1b2c3d4e5";

    [Theory]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundException))]
    [InlineData(HttpStatusCode.Conflict, typeof(ConflictException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(AuthException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(AuthException))]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(NachosValidationException))]
    public async Task DomainStatus_MapsToAbstractionsException_WithDetailAsMessage(HttpStatusCode status, Type expected)
    {
        var ex = await CaptureAsync(status, $$"""{"detail":"the detail","type":"about:blank","title":"t","status":{{(int)status}}}""");

        ex.GetType().ShouldBe(expected);
        ex.Message.ShouldBe("the detail");
    }

    [Fact]
    public async Task DomainValidation_CarriesDetail()
    {
        var ex = await CaptureAsync(HttpStatusCode.UnprocessableEntity, """{"detail":"key issuance requires configured signing keys"}""");

        ex.ShouldBeOfType<NachosValidationException>().Detail.ShouldBe("key issuance requires configured signing keys");
    }

    [Fact]
    public async Task RequestValidation_ArrayDetail_MapsLocMsgType()
    {
        var ex = await CaptureAsync(
            HttpStatusCode.UnprocessableEntity,
            """{"detail":[{"loc":["body","messages",0,"content"],"msg":"String should have at most 25000 characters","type":"string_too_long","ctx":{"max_length":25000},"input":"xx"},{"loc":["query","size"],"msg":"too big","type":"less_than_equal"}],"type":"about:blank","status":422}""");

        var errors = ex.ShouldBeOfType<RequestValidationException>().Errors;
        errors.Count.ShouldBe(2);
        errors[0].Loc.ShouldBe(["body", "messages", 0, "content"]);
        errors[0].Msg.ShouldBe("String should have at most 25000 characters");
        errors[0].Type.ShouldBe("string_too_long");
        errors[1].Loc.ShouldBe(["query", "size"]);
    }

    [Fact]
    public async Task IdempotencyKeyReuse_MapsToIdempotencyKeyReusedException()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(
            HttpStatusCode.UnprocessableEntity,
            """{"detail":"Idempotency-Key was already used with a different request","type":"https://nachos.dev/errors/idempotency-key-reused","title":"Unprocessable Entity","status":422}"""));

        var ex = await Should.ThrowAsync<IdempotencyKeyReusedException>(
            () => Client(stub).CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")], "k1"));

        ex.Message.ShouldBe("Idempotency-Key was already used with a different request");
    }

    [Fact]
    public async Task OtherStringDetail422_OnMessageCreate_IsDomainValidation()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.UnprocessableEntity, """{"detail":"peer id invalid","type":"about:blank"}"""));

        await Should.ThrowAsync<NachosValidationException>(
            () => Client(stub).CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "a b")]));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task UnmappedStatus_IsHttpRequestExceptionWithStatus(HttpStatusCode status)
    {
        var ex = await CaptureAsync(status, """{"detail":"boom"}""");

        var http = ex.ShouldBeOfType<HttpRequestException>();
        http.StatusCode.ShouldBe(status);
        http.Message.ShouldContain("boom");
        http.Message.ShouldContain("GET /v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundException))]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(NachosValidationException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(AuthException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(HttpRequestException))]
    public async Task NonJsonErrorBody_StillMapsByStatus(HttpStatusCode status, Type expected)
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent("<html>proxy error</html>", Encoding.UTF8, "text/html"),
        });

        var ex = await Should.ThrowAsync<Exception>(() => Client(stub).GetMessageAsync("w1", "s1", "m1"));

        ex.GetType().ShouldBe(expected);
        ex.Message.ShouldContain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EmptyErrorBody_StillMapsByStatus()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Should.ThrowAsync<NotFoundException>(() => Client(stub).GetMessageAsync("w1", "s1", "m1"));

        ex.Message.ShouldContain("404");
    }

    /// <summary>A server (or proxy) that echoes the bearer token back must not leak it into exception text.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task EchoedApiKey_NeverAppearsInExceptionText(HttpStatusCode status)
    {
        var ex = await CaptureAsync(status, $$"""{"detail":"invalid token Bearer {{ApiKey}}"}""");

        ex.ToString().ShouldNotContain(ApiKey);
        ex.Message.ShouldContain("[redacted]");
        if (ex is NachosValidationException validation)
        {
            validation.Detail.ShouldNotContain(ApiKey);
        }
    }

    [Fact]
    public async Task EchoedApiKey_NeverAppearsInValidationErrors()
    {
        var ex = await CaptureAsync(
            HttpStatusCode.UnprocessableEntity,
            $$"""{"detail":[{"loc":["header","authorization"],"msg":"bad token {{ApiKey}}","type":"value_error"}]}""");

        var errors = ex.ShouldBeOfType<RequestValidationException>().Errors;
        errors.Single().Msg.ShouldNotContain(ApiKey);
        ex.ToString().ShouldNotContain(ApiKey);
    }

    [Fact]
    public async Task EchoedApiKey_NeverAppearsInIdempotencyError()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(
            HttpStatusCode.UnprocessableEntity,
            $$"""{"detail":"reused by {{ApiKey}}","type":"https://nachos.dev/errors/idempotency-key-reused"}"""));

        var ex = await Should.ThrowAsync<IdempotencyKeyReusedException>(
            () => Client(stub).CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")]));

        ex.ToString().ShouldNotContain(ApiKey);
    }

    private static NachosHttpClient Client(StubHandler stub) =>
        new NachosHttpClient(new HttpClient(stub), new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/"), ApiKey = ApiKey });

    private static async Task<Exception> CaptureAsync(HttpStatusCode status, string body)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(status, body));
        return await Should.ThrowAsync<Exception>(() => Client(stub).GetMessageAsync("w1", "s1", "m1"));
    }
}
