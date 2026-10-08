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
            """{"detail":"Idempotency-Key was already used with a different request","type":"urn:nachos:problem:idempotency-key-reused","title":"Unprocessable Entity","status":422}"""));

        var ex = await Should.ThrowAsync<IdempotencyKeyReusedException>(
            () => Client(stub).CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")], "k1"));

        ex.Message.ShouldBe("Idempotency-Key was already used with a different request");
    }

    [Fact]
    public async Task IdempotencyKeyReuse_MatchesTheSharedProblemTypeConstant()
    {
        var ex = await CaptureAsync(
            HttpStatusCode.UnprocessableEntity, $$"""{"detail":"reused","type":"{{ProblemTypes.IdempotencyKeyReused}}"}""");

        ex.ShouldBeOfType<IdempotencyKeyReusedException>();
    }

    /// <summary>Spec §16: the problem type is matched exactly, never by its last segment.</summary>
    [Theory]
    [InlineData("urn:unrelated:problem:idempotency-key-reused")]
    [InlineData("https://errors.example/problems/idempotency-key-reused")]
    [InlineData("https://errors.example/problems#idempotency-key-reused")]
    [InlineData("idempotency-key-reused")]
    [InlineData("urn:nachos:problem:idempotency-key-reused-v2")]
    [InlineData("urn:nachos:problem:idempotency-key-reused/extra")]
    [InlineData("urn:nachos:problem:idempotency-key-reused ")]
    [InlineData("URN:NACHOS:PROBLEM:IDEMPOTENCY-KEY-REUSED")]
    [InlineData("urn:nachos:problem:other")]
    public async Task OtherProblemType_On422_IsDomainValidation(string type)
    {
        var ex = await CaptureAsync(HttpStatusCode.UnprocessableEntity, $$"""{"detail":"d","type":"{{type}}"}""");

        ex.ShouldBeOfType<NachosValidationException>();
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
            $$"""{"detail":"reused by {{ApiKey}}","type":"urn:nachos:problem:idempotency-key-reused","title":"Unprocessable Entity","status":422}"""));

        var ex = await Should.ThrowAsync<IdempotencyKeyReusedException>(
            () => Client(stub).CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")]));

        ex.ToString().ShouldNotContain(ApiKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundException))]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(NachosValidationException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(AuthException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(HttpRequestException))]
    public async Task DuplicateKeyErrorBody_StillMapsByStatus(HttpStatusCode status, Type expected)
    {
        var ex = await CaptureAsync(status, """{"detail":"x","detail":"y"}""");

        ex.GetType().ShouldBe(expected);
        ex.Message.ShouldContain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HugeDetail_IsTruncated(HttpStatusCode status)
    {
        var ex = await CaptureAsync(status, $$"""{"detail":"{{new string('x', 5_000_000)}}"}""");

        ex.Message.Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
        ex.Message.ShouldEndWith(ErrorMapper.TruncationMarker);
        if (ex is NachosValidationException validation)
        {
            validation.Detail.Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
        }
    }

    [Fact]
    public async Task HugeValidationMsg_IsTruncated()
    {
        var ex = await CaptureAsync(
            HttpStatusCode.UnprocessableEntity,
            $$"""{"detail":[{"loc":["body"],"msg":"{{new string('m', 100_000)}}","type":"value_error"}]}""");

        ex.ShouldBeOfType<RequestValidationException>().Errors.Single().Msg.Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
    }

    /// <summary>Redaction runs before truncation, so a key straddling the cut is never partly kept.</summary>
    [Fact]
    public async Task KeyAtTruncationBoundary_IsNotPartiallyLeaked()
    {
        // The cut falls 6 characters into the key: truncating first would keep "nk-SEC" and then fail to redact it.
        var keyStart = ErrorMapper.MaxMessageLength - ErrorMapper.TruncationMarker.Length - 6;
        var detail = new string('x', keyStart) + ApiKey + new string('y', 100);

        var ex = await CaptureAsync(HttpStatusCode.NotFound, $$"""{"detail":"{{detail}}"}""");

        ex.Message.ShouldNotContain(ApiKey[..6]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Truncation_NeverSplitsASurrogatePair(int offset)
    {
        // Emoji (two UTF-16 units each) laid so that the cut lands on a pair boundary or inside a pair.
        var cut = ErrorMapper.MaxMessageLength - ErrorMapper.TruncationMarker.Length;
        var detail = new string('x', cut - offset) + string.Concat(Enumerable.Repeat("\U0001F600", 100));

        var ex = await CaptureAsync(HttpStatusCode.NotFound, $$"""{"detail":"{{detail}}"}""");

        ex.Message.ShouldEndWith(ErrorMapper.TruncationMarker);
        var kept = ex.Message[..^ErrorMapper.TruncationMarker.Length];
        char.IsHighSurrogate(kept[^1]).ShouldBeFalse("a lone high surrogate was left before the marker");
        kept.EnumerateRunes().ShouldAllBe(r => r != System.Text.Rune.ReplacementChar);
    }

    [Fact]
    public async Task EchoedApiKey_InValidationLocAndType_IsRedacted()
    {
        var ex = await CaptureAsync(
            HttpStatusCode.UnprocessableEntity,
            $$"""{"detail":[{"loc":["header","{{ApiKey}}",3],"msg":"bad","type":"t-{{ApiKey}}"}]}""");

        var error = ex.ShouldBeOfType<RequestValidationException>().Errors.Single();
        error.Loc.ShouldBe(["header", $"{ErrorMapper.Redacted}", 3]);
        error.Type.ShouldBe($"t-{ErrorMapper.Redacted}");
        ex.ToString().ShouldNotContain(ApiKey);
    }

    [Fact]
    public async Task HugeValidationLocAndType_AreTruncated()
    {
        var huge = new string('z', 100_000);
        var ex = await CaptureAsync(
            HttpStatusCode.UnprocessableEntity,
            $$"""{"detail":[{"loc":["body","{{huge}}"],"msg":"m","type":"{{huge}}"}]}""");

        var error = ex.ShouldBeOfType<RequestValidationException>().Errors.Single();
        ((string)error.Loc[1]).Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
        error.Type.Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
    }

    [Fact]
    public async Task ValidationErrorCount_IsCapped_WithAMarkerEntry()
    {
        var body = new System.Text.StringBuilder("""{"detail":[""");
        for (var i = 0; i < 100_000; i++)
        {
            body.Append(i == 0 ? string.Empty : ",").Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $$"""{"loc":["body","messages",{{i}},"{{ApiKey}}"],"msg":"m {{ApiKey}}","type":"t-{{ApiKey}}"}""");
        }

        var ex = await CaptureAsync(HttpStatusCode.UnprocessableEntity, body.Append("]}").ToString());

        var errors = ex.ShouldBeOfType<RequestValidationException>().Errors;
        errors.Count.ShouldBe(ErrorMapper.MaxValidationErrors + 1);
        errors[ErrorMapper.MaxValidationErrors - 1].Loc[2].ShouldBe(ErrorMapper.MaxValidationErrors - 1);
        var marker = errors[^1];
        marker.Type.ShouldBe(ErrorMapper.OmittedErrorsType);
        marker.Msg.ShouldContain((100_000 - ErrorMapper.MaxValidationErrors).ToString(System.Globalization.CultureInfo.InvariantCulture));
        ex.ToString().ShouldNotContain(ApiKey);
        errors.ShouldAllBe(e => !e.Type.Contains(ApiKey) && !e.Msg.Contains(ApiKey));
    }

    [Fact]
    public async Task ValidationErrorCount_AtTheCap_HasNoMarker()
    {
        var items = Enumerable.Range(0, ErrorMapper.MaxValidationErrors)
            .Select(i => $$"""{"loc":["body",{{i}}],"msg":"m","type":"t"}""");
        var ex = await CaptureAsync(HttpStatusCode.UnprocessableEntity, $$"""{"detail":[{{string.Join(",", items)}}]}""");

        var errors = ex.ShouldBeOfType<RequestValidationException>().Errors;
        errors.Count.ShouldBe(ErrorMapper.MaxValidationErrors);
        errors.ShouldAllBe(e => e.Type == "t");
    }

    [Fact]
    public async Task ValidationLocComponents_AreCapped_WithAMarkerComponent()
    {
        var huge = new string('z', 100_000);
        var parts = string.Join(",", Enumerable.Range(0, 100_000).Select(i => i % 2 == 0 ? $"\"{huge[..10]}{ApiKey}\"" : "7"));
        var ex = await CaptureAsync(
            HttpStatusCode.UnprocessableEntity, $$"""{"detail":[{"loc":[{{parts}}],"msg":"m","type":"t"}]}""");

        var loc = ex.ShouldBeOfType<RequestValidationException>().Errors.Single().Loc;
        loc.Count.ShouldBe(ErrorMapper.MaxLocComponents + 1);
        loc[1].ShouldBe(7);
        loc[ErrorMapper.MaxLocComponents - 1].ShouldBe(7);
        var marker = loc[^1].ShouldBeOfType<string>();
        marker.ShouldContain((100_000 - ErrorMapper.MaxLocComponents).ToString(System.Globalization.CultureInfo.InvariantCulture));
        marker.ShouldStartWith(ErrorMapper.TruncationMarker);
        ex.ToString().ShouldNotContain(ApiKey);
    }

    [Fact]
    public async Task ValidationLocComponents_AtTheCap_HaveNoMarker()
    {
        var parts = string.Join(",", Enumerable.Range(0, ErrorMapper.MaxLocComponents));
        var ex = await CaptureAsync(
            HttpStatusCode.UnprocessableEntity, $$"""{"detail":[{"loc":[{{parts}}],"msg":"m","type":"t"}]}""");

        var loc = ex.ShouldBeOfType<RequestValidationException>().Errors.Single().Loc;
        loc.ShouldBe(Enumerable.Range(0, ErrorMapper.MaxLocComponents).Cast<object>().ToArray());
    }

    private static NachosHttpClient Client(StubHandler stub) =>
        new NachosHttpClient(new HttpClient(stub), new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/"), ApiKey = ApiKey });

    private static async Task<Exception> CaptureAsync(HttpStatusCode status, string body)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(status, body));
        return await Should.ThrowAsync<Exception>(() => Client(stub).GetMessageAsync("w1", "s1", "m1"));
    }
}
