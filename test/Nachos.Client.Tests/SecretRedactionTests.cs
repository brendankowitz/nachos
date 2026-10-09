using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// <see cref="SecretRedaction"/> and <see cref="RedactionSecrets"/> directly: what counts as a mention, what a rebuilt
/// chain keeps, and that redaction is single-pass (no marker growth) and never touches library-owned text.
/// </summary>
public sealed class SecretRedactionTests
{
    private const string Secret = "nk-SECRET-unit-0042";

    private static readonly RedactionSecrets Secrets = RedactionSecrets.Of(Secret);

    private static readonly string DashHex = BitConverter.ToString(Encoding.UTF8.GetBytes(Secret));

    private static readonly string Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(Secret));

    [Theory]
    [InlineData("plain")]
    [InlineData("dash hex upper")]
    [InlineData("dash hex lower")]
    [InlineData("contiguous hex upper")]
    [InlineData("contiguous hex lower")]
    public void EveryForm_IsRedacted_InsideLongerText(string form)
    {
        var echoed = form switch
        {
            "plain" => Secret,
            "dash hex upper" => DashHex,
            "dash hex lower" => DashHex.ToLowerInvariant(),
            "contiguous hex upper" => Hex,
            _ => Hex.ToLowerInvariant(),
        };

        var redacted = Secrets.Redact($"invalid chunk extension: '72-65-72-20-{echoed}-0D'");

        redacted.ShouldBe($"invalid chunk extension: '72-65-72-20-{ErrorMapper.Redacted}-0D'");
    }

    [Fact]
    public void PlainText_IsMatchedExactly_NotInAnotherCase()
    {
        Secrets.Redact(Secret.ToUpperInvariant()).ShouldBe(Secret.ToUpperInvariant());
    }

    [Fact]
    public void Redaction_IsSinglePass_AndTheMarkerNeverGrows()
    {
        var secrets = RedactionSecrets.Of("redact");

        var once = secrets.Redact("a redact b [redacted] redacted");
        var twice = secrets.Redact(once);

        once.ShouldBe("a [redacted] b [redacted] [redacted]ed");
        twice.ShouldBe(once);
    }

    [Fact]
    public void OverlappingMatches_CollapseIntoOneMarker()
    {
        RedactionSecrets.Of("abc", "bcd").Redact("xabcdx").ShouldBe("x[redacted]x");
    }

    [Fact]
    public void AuthorizationThatDoesNotParse_IsStillFound()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://nachos.test/");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer nk-a,\"b\"-c");

        var secrets = RedactionSecrets.FromAuthorization(request);

        secrets.Redact("echo 'Bearer nk-a,\"b\"-c'").ShouldBe("echo 'Bearer [redacted]'");
    }

    [Fact]
    public void SecretOnlyInData_IsAMention_AndTheValueIsRedacted()
    {
        var ex = new HttpRequestException("clean");
        ex.Data["echo"] = "value " + Secret;

        SecretRedaction.Mentions(ex, Secrets).ShouldBeTrue();
        var redacted = SecretRedaction.Redact(ex, Secrets);

        redacted.Message.ShouldBe("clean");
        redacted.Data["echo"].ShouldBe("value " + ErrorMapper.Redacted);
    }

    [Fact]
    public void SecretOnlyInAnAggregatesSecondInnerException_IsAMention()
    {
        var second = new IOException("clean two");
        second.Data["echo"] = Secret;
        var ex = new HttpRequestException("clean", new AggregateException(new IOException("clean one"), second));

        SecretRedaction.Mentions(ex, Secrets).ShouldBeTrue();
        var aggregate = SecretRedaction.Redact(ex, Secrets).InnerException.ShouldBeOfType<AggregateException>();

        aggregate.InnerExceptions[1].Data["echo"].ShouldBe(ErrorMapper.Redacted);
    }

    [Fact]
    public void DataKeys_AreRedacted_ExceptTheLibrarysOwn()
    {
        var ex = new HttpRequestException("clean");
        ex.Data["key " + Secret] = 1;
        ex.Data[NachosExceptionData.RetryAfter] = TimeSpan.FromSeconds(3);
        ex.Data["Nachos.Other"] = "library text mentioning " + Secret;

        var redacted = SecretRedaction.Redact(ex, Secrets);

        redacted.Data.Contains("key " + ErrorMapper.Redacted).ShouldBeTrue();
        redacted.Data.Contains("key " + Secret).ShouldBeFalse();
        redacted.Data[NachosExceptionData.RetryAfter].ShouldBe(TimeSpan.FromSeconds(3));
        redacted.Data["Nachos.Other"].ShouldBe("library text mentioning " + Secret);
    }

    [Fact]
    public void LibraryDataAlone_IsNotAMention()
    {
        var ex = new HttpRequestException("clean");
        ex.Data["Nachos.Other"] = Secret;

        SecretRedaction.Mentions(ex, Secrets).ShouldBeFalse();
    }

    [Fact]
    public void TaskCanceledException_KeepsItsTypeAndToken()
    {
        using var cts = new CancellationTokenSource();
        var ex = new TaskCanceledException("canceled " + Secret, null, cts.Token);

        var redacted = SecretRedaction.Redact(ex, Secrets).ShouldBeOfType<TaskCanceledException>();

        redacted.CancellationToken.ShouldBe(cts.Token);
        redacted.Message.ShouldBe("canceled " + ErrorMapper.Redacted);
    }

    [Fact]
    public void OperationCanceledException_KeepsItsTypeAndToken()
    {
        using var cts = new CancellationTokenSource();
        var ex = new OperationCanceledException("canceled " + Secret, cts.Token);

        var redacted = SecretRedaction.Redact(ex, Secrets).ShouldBeOfType<OperationCanceledException>();

        redacted.CancellationToken.ShouldBe(cts.Token);
    }

    [Fact]
    public void HttpRequestException_KeepsItsErrorAndStatus()
    {
        var ex = new HttpRequestException(HttpRequestError.InvalidResponse, "bad " + Secret, null, HttpStatusCode.BadGateway);

        var redacted = SecretRedaction.Redact(ex, Secrets).ShouldBeOfType<HttpRequestException>();

        redacted.HttpRequestError.ShouldBe(HttpRequestError.InvalidResponse);
        redacted.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public void HttpIOException_KeepsItsTypeAndError()
    {
        var ex = new HttpIOException(HttpRequestError.InvalidResponse, "Received an invalid chunk extension: '" + DashHex + "'.");

        var redacted = SecretRedaction.Redact(ex, Secrets).ShouldBeOfType<HttpIOException>();

        redacted.HttpRequestError.ShouldBe(HttpRequestError.InvalidResponse);
        redacted.Message.ShouldNotContain(DashHex);
    }

    [Fact]
    public void SecretFreeInnerException_KeepsItsIdentity_InsideARebuiltChain()
    {
        var inner = new IOException("clean inner");
        var ex = new HttpRequestException("outer " + Secret, inner);

        SecretRedaction.Redact(ex, Secrets).InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void SecretFreeException_IsReturnedAsItIs()
    {
        var ex = new HttpRequestException("clean", new IOException("clean inner"));

        SecretRedaction.Redact(ex, Secrets).ShouldBeSameAs(ex);
    }

    [Fact]
    public void RebuiltMessage_IsBounded()
    {
        var ex = new HttpRequestException(Secret + new string('z', 5000));

        var message = SecretRedaction.Redact(ex, Secrets).Message;

        message.Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
        message.ShouldEndWith(ErrorMapper.TruncationMarker);
    }

    [Fact]
    public void OtherExceptionTypes_BecomeIOException_NamingTheOriginalType()
    {
        var redacted = SecretRedaction.Redact(new InvalidOperationException("bad " + Secret), Secrets);

        redacted.ShouldBeOfType<IOException>().Message.ShouldBe("System.InvalidOperationException: bad " + ErrorMapper.Redacted);
    }

    [Fact]
    public void SanitizedException_IsNeverExaminedOrRebuilt()
    {
        var ex = SecretRedaction.MarkSanitized(new HttpRequestException("library text " + Secret));

        SecretRedaction.Mentions(ex, Secrets).ShouldBeFalse();
        SecretRedaction.Redact(ex, Secrets).ShouldBeSameAs(ex);
    }

    [Fact]
    public void VeryDeepChain_IsRedactedWithoutOverflow_AndCapped()
    {
        Exception ex = new IOException("leaf " + Secret);
        for (var i = 0; i < 20_000; i++)
        {
            ex = new IOException($"level {i} {Secret}", ex);
        }

        var redacted = SecretRedaction.Redact(ex, Secrets);

        var depth = 0;
        for (var current = redacted; current is not null; current = current.InnerException)
        {
            current.Message.ShouldNotContain(Secret);
            depth++;
        }

        depth.ShouldBe(SecretRedaction.MaxRebuiltDepth);
    }

    [Theory]
    [InlineData("Nachos")]
    [InlineData("Retry")]
    [InlineData("redact")]
    [InlineData("a")]
    public async Task SecretsThatLookLikeLibraryText_LeaveTheSuffixAndDataIntact_OnAMappedStatus(string apiKey)
    {
        var stub = new StubHandler((_, _) =>
        {
            var response = StubHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"detail":"busy: {{apiKey}} Nachos Retry redact [redacted] a"}""");
            response.Headers.TryAddWithoutValidation("Retry-After", "40");
            return response;
        });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(stub, apiKey).GetMessageAsync("w1", "s1", "m1"));

        AssertLibraryTextIntact(ex, TimeSpan.FromSeconds(40));
        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData("Nachos")]
    [InlineData("Retry")]
    [InlineData("redact")]
    [InlineData("a")]
    public async Task SecretsThatLookLikeLibraryText_LeaveTheSuffixAndDataIntact_OnAWrappedBodyFailure(string apiKey)
    {
        var stub = new StubHandler((_, _) =>
        {
            var content = new StreamContent(new FailingStream($"reset after echoing {apiKey} redact [redacted]"));
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content };
            response.Headers.TryAddWithoutValidation("Retry-After", "40");
            return response;
        });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(stub, apiKey).GetMessageAsync("w1", "s1", "m1"));

        AssertLibraryTextIntact(ex, TimeSpan.FromSeconds(40));
        ex.InnerException!.Message.ShouldNotContain($"echoing {apiKey} ");
        stub.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("Nachos")]
    [InlineData("Retry")]
    [InlineData("redact")]
    public async Task SecretFreeCallerCancellation_KeepsItsIdentity(string apiKey)
    {
        using var cts = new CancellationTokenSource();
        var thrown = new OperationCanceledException("The operation was canceled.", cts.Token);
        var stub = new StubHandler((_, _) =>
        {
            cts.Cancel();
            throw thrown;
        });
        using var invoker = new HttpMessageInvoker(new RetryHandler(TimeProvider.System) { InnerHandler = stub });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://nachos.test/v3/x");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Options.Set(RetryHandler.RouteTemplate, "/v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}");

        Exception? caught = null;
        try
        {
            await invoker.SendAsync(request, cts.Token);
        }
        catch (Exception e)
        {
            caught = e;
        }

        caught.ShouldBeSameAs(thrown);
    }

    private static NachosHttpClient Client(HttpMessageHandler stub, string apiKey) =>
        new(new HttpClient(new RetryHandler(TimeProvider.System, (_, _) => Task.CompletedTask, () => 0) { InnerHandler = stub }),
            new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/"), ApiKey = apiKey });

    private static void AssertLibraryTextIntact(Exception ex, TimeSpan retryAfter)
    {
        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
        delay.ShouldBe(retryAfter);
        ex.Message.ShouldEndWith($" Retry-After: {(int)retryAfter.TotalSeconds}s.");
        for (var current = ex; current is not null; current = current.InnerException)
        {
            // The marker never nests or grows: no "[[redacted]", "[redacted]ed]" or "]]".
            Regex.IsMatch(current.Message, @"\[\[|\]ed\]|\]\]").ShouldBeFalse(current.Message);
        }
    }

    /// <summary>Fails the first read with an <see cref="IOException"/> carrying <paramref name="message"/>.</summary>
    private sealed class FailingStream(string message) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(message);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException(message);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
