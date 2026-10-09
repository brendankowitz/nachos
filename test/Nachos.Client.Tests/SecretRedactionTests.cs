using System.Net;
using System.Net.Sockets;
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
    public void ShortSecrets_HaveNoHexForms_SoDigitsAreLeftAlone()
    {
        // "zz" is 7A7A / 7A-7A: shorter than the hex minimum, so only the plain text is matched.
        RedactionSecrets.Of("zz").Redact("status 7a7a 7A-7A zz").ShouldBe("status 7a7a 7A-7A " + ErrorMapper.Redacted);
    }

    [Fact]
    public void HexForms_AreMatchedFromTheMinimumLength()
    {
        // "abc" is 61-62-63 (8 characters, matched) and 616263 (6, not matched).
        var secrets = RedactionSecrets.Of("abc");

        secrets.Redact("61-62-63").ShouldBe(ErrorMapper.Redacted);
        secrets.Redact("616263").ShouldBe("616263");
    }

    [Theory]
    [InlineData("bearer nk-tok-123456")]
    [InlineData("  Bearer nk-tok-123456  ")]
    [InlineData("BEARER   nk-tok-123456")]
    public void SchemeIsStripped_InAnyCase_AndPaddingIsIgnored(string authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://nachos.test/");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        RedactionSecrets.FromAuthorization(request).Redact("echo nk-tok-123456!").ShouldBe("echo " + ErrorMapper.Redacted + "!");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    [InlineData(1555, false)]
    public void ForHeaderNames_IgnoresBearerValuesShorterThanTheMinimum(int length, bool ignored)
    {
        var value = new string('k', length);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://nachos.test/");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + value);

        var secrets = RedactionSecrets.ForHeaderNames(request);

        secrets.IsEmpty.ShouldBe(ignored);
        secrets.OccursIn("X-" + value).ShouldBe(!ignored);
        RedactionSecrets.FromAuthorization(request).OccursIn("X-" + value).ShouldBeTrue("mapped text keeps plain matching at any length");
    }

    [Fact]
    public void OccursIn_IgnoresTheMarkerItself()
    {
        var secrets = RedactionSecrets.Of("redact");

        secrets.OccursIn("[redacted]").ShouldBeFalse();
        secrets.OccursIn("x redact y").ShouldBeTrue();
        secrets.OccursIn("[redacted] redact").ShouldBeTrue();
    }

    [Fact]
    public void ApiKeyContainingTheMarker_IsRejectedWithoutEchoingIt()
    {
        var ex = Should.Throw<ArgumentException>(() => new NachosHttpClient(
            new HttpClient(), new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/"), ApiKey = "nk-[redacted]-key" }));

        ex.Message.ShouldContain("redaction marker");
        ex.Message.ShouldNotContain("nk-");
    }

    // Fail closed: failures whose text can repeat server bytes are replaced, whatever they say.

    [Fact]
    public void HttpIOException_IsReplaced_KeepingItsError_EvenWithoutASecret()
    {
        var ex = new HttpIOException(HttpRequestError.InvalidResponse, "Received an invalid chunk extension: '79-4A-68'.");

        var replaced = SecretRedaction.Sanitize(ex, Secrets).ShouldBeOfType<HttpRequestException>();

        replaced.HttpRequestError.ShouldBe(HttpRequestError.InvalidResponse);
        replaced.Message.ShouldBe(SecretRedaction.CannedMessage(HttpRequestError.InvalidResponse));
        replaced.InnerException.ShouldBeNull();
    }

    [Fact]
    public void HttpRequestException_WithAServerError_IsReplaced_KeepingErrorAndStatus()
    {
        var ex = new HttpRequestException(HttpRequestError.InvalidResponse, "bad line", new IOException("raw"), HttpStatusCode.BadGateway);

        var replaced = SecretRedaction.Sanitize(ex, Secrets).ShouldBeOfType<HttpRequestException>();

        replaced.HttpRequestError.ShouldBe(HttpRequestError.InvalidResponse);
        replaced.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        replaced.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData("io")]
    [InlineData("format")]
    [InlineData("invalid operation")]
    [InlineData("unknown http error")]
    public void AnythingNotKnownSafe_IsReplaced(string kind)
    {
        Exception ex = kind switch
        {
            "io" => new IOException("reset after 'eyJhbGci'"),
            "format" => new FormatException("bad 'eyJhbGci'"),
            "invalid operation" => new InvalidOperationException("odd 'eyJhbGci'"),
            _ => new HttpRequestException("odd 'eyJhbGci'"),
        };

        var replaced = SecretRedaction.Sanitize(ex, RedactionSecrets.None).ShouldBeOfType<HttpRequestException>();

        replaced.Message.ShouldBe(SecretRedaction.CannedMessage(HttpRequestError.Unknown));
    }

    [Theory]
    [InlineData("invalid operation", typeof(InvalidOperationException))]
    [InlineData("io", typeof(IOException))]
    [InlineData("http", typeof(HttpRequestException))]
    public void Replacement_RemembersTheTypeItReplaced(string kind, Type original)
    {
        Exception ex = kind switch
        {
            "invalid operation" => new InvalidOperationException("odd"),
            "io" => new IOException("raw"),
            _ => new HttpRequestException(HttpRequestError.InvalidResponse, "bad line"),
        };

        var replaced = SecretRedaction.Sanitize(ex, Secrets);

        replaced.ShouldBeOfType<HttpRequestException>();
        SecretRedaction.ReplacedType(replaced).ShouldBe(original);
        SecretRedaction.ReplacedType(ex).ShouldBeNull();
    }

    [Fact]
    public void KeptAndRebuiltFailures_AreNotReplacements()
    {
        var kept = new OperationCanceledException("canceled");
        var rebuilt = SecretRedaction.Sanitize(new TimeoutException("slow " + Secret), Secrets);

        SecretRedaction.ReplacedType(SecretRedaction.Sanitize(kept, Secrets)).ShouldBeNull();
        SecretRedaction.ReplacedType(rebuilt).ShouldBeNull();
    }

    [Fact]
    public void Replacement_KeepsOnlyLibraryData()
    {
        var ex = new HttpIOException(HttpRequestError.ResponseEnded, "ended");
        ex.Data[NachosExceptionData.RetryAfter] = TimeSpan.FromSeconds(4);
        ex.Data["other"] = "server said eyJhbGci";

        var replaced = SecretRedaction.Sanitize(ex, Secrets);

        replaced.Data[NachosExceptionData.RetryAfter].ShouldBe(TimeSpan.FromSeconds(4));
        replaced.Data.Contains("other").ShouldBeFalse();
    }

    /// <summary>
    /// .NET names the target host and port in a connection failure, and a followed redirect lets the server choose that
    /// host, so the kept failure is rebuilt with fixed text: error and socket error, no host. The inner socket
    /// exception is framework text and keeps its identity.
    /// </summary>
    [Theory]
    [InlineData(HttpRequestError.ConnectionError, 10061, SocketError.ConnectionRefused)]
    [InlineData(HttpRequestError.NameResolutionError, 11001, SocketError.HostNotFound)]
    public void ConnectionFailure_IsRebuilt_WithoutTheHost_KeepingErrorStatusAndInner(HttpRequestError error, int nativeError, SocketError socketError)
    {
        var inner = new SocketException(nativeError);
        var hexHost = Convert.ToHexString(Encoding.UTF8.GetBytes(Secret)).ToLowerInvariant();
        var ex = new HttpRequestException(error, $"Name or service not known ({hexHost[..20]}.{hexHost[20..]}:80)", inner, HttpStatusCode.BadGateway);

        var rebuilt = SecretRedaction.Sanitize(ex, RedactionSecrets.None).ShouldBeOfType<HttpRequestException>();

        rebuilt.ShouldNotBeSameAs(ex);
        rebuilt.HttpRequestError.ShouldBe(error);
        rebuilt.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        rebuilt.InnerException.ShouldBeSameAs(inner);
        rebuilt.Message.ShouldBe(SecretRedaction.ConnectionMessage(error, socketError));
        rebuilt.Message.ShouldBe($"The Nachos HTTP connection failed ({error}, {socketError}). The target host and port are withheld because a redirect lets the server choose them.");
        SecretScan.FindLeak(rebuilt.ToString(), Secret, window: 8).ShouldBeNull();
    }

    [Fact]
    public void ConnectionFailure_WithoutASocketException_NamesOnlyTheError()
    {
        var ex = new HttpRequestException(HttpRequestError.ConnectionError, "refused (nachos.test:443)");

        SecretRedaction.Sanitize(ex, Secrets).Message.ShouldBe(
            "The Nachos HTTP connection failed (ConnectionError). The target host and port are withheld because a redirect lets the server choose them.");
    }

    [Fact]
    public void SanitizedConnectionFailure_IsKept_AsTheSameInstance()
    {
        var ex = SecretRedaction.Sanitize(
            new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (nachos.test:443)", new SocketException(10061)), Secrets);

        SecretRedaction.Sanitize(ex, Secrets).ShouldBeSameAs(ex);
    }

    [Fact]
    public void ConnectionFailure_OverAnUnsafeInner_IsReplaced()
    {
        var ex = new HttpRequestException(HttpRequestError.ConnectionError, "refused", new IOException("raw"));

        SecretRedaction.Sanitize(ex, Secrets).ShouldNotBeSameAs(ex);
    }

    [Fact]
    public void CancellationAroundAnUnsafeChain_KeepsTypeAndToken_AndGetsTheReplacementInside()
    {
        using var cts = new CancellationTokenSource();
        var ex = new TaskCanceledException("The operation was canceled.", new HttpIOException(HttpRequestError.InvalidResponse, "raw " + Secret), cts.Token);

        var sanitized = SecretRedaction.Sanitize(ex, Secrets).ShouldBeOfType<TaskCanceledException>();

        sanitized.CancellationToken.ShouldBe(cts.Token);
        sanitized.Message.ShouldBe("The operation was canceled.");
        sanitized.InnerException.ShouldBeOfType<HttpRequestException>().Message.ShouldBe(SecretRedaction.CannedMessage(HttpRequestError.InvalidResponse));
    }

    [Fact]
    public void AggregateWithAnUnsafeInner_IsRebuilt_KeepingTheSafeOne()
    {
        var safe = new TimeoutException("slow");
        var ex = new AggregateException(safe, new IOException("raw " + Secret));

        var aggregate = SecretRedaction.Sanitize(ex, Secrets).ShouldBeOfType<AggregateException>();

        aggregate.InnerExceptions[0].ShouldBeSameAs(safe);
        aggregate.InnerExceptions[1].ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    public void SanitizedException_IsKept()
    {
        var ex = SecretRedaction.MarkSanitized(new HttpIOException(HttpRequestError.InvalidResponse, "library text"));

        SecretRedaction.Sanitize(ex, Secrets).ShouldBeSameAs(ex);
    }

    // Second layer: the kept (safe) types still have the secrets redacted from them.

    [Fact]
    public void SafeChain_WithoutASecret_IsReturnedAsItIs()
    {
        var ex = new OperationCanceledException("canceled", new TimeoutException("slow"));

        SecretRedaction.Sanitize(ex, Secrets).ShouldBeSameAs(ex);
    }

    [Fact]
    public void SecretOnlyInData_IsRedacted()
    {
        var ex = new OperationCanceledException("canceled");
        ex.Data["echo"] = "value " + Secret;

        var redacted = SecretRedaction.Sanitize(ex, Secrets);

        redacted.ShouldNotBeSameAs(ex);
        redacted.Message.ShouldBe("canceled");
        redacted.Data["echo"].ShouldBe("value " + ErrorMapper.Redacted);
    }

    [Fact]
    public void SecretOnlyInAnAggregatesSecondInnerException_IsRedacted()
    {
        var second = new TimeoutException("slow two");
        second.Data["echo"] = Secret;
        var ex = new OperationCanceledException("canceled", new AggregateException(new TimeoutException("slow one"), second));

        var aggregate = SecretRedaction.Sanitize(ex, Secrets).InnerException.ShouldBeOfType<AggregateException>();

        aggregate.InnerExceptions[1].Data["echo"].ShouldBe(ErrorMapper.Redacted);
    }

    [Fact]
    public void DataKeys_AreRedacted_ExceptTheLibrarysOwn()
    {
        var ex = new OperationCanceledException("canceled");
        ex.Data["key " + Secret] = 1;
        ex.Data[NachosExceptionData.RetryAfter] = TimeSpan.FromSeconds(3);
        ex.Data["Nachos.Other"] = "library text mentioning " + Secret;

        var redacted = SecretRedaction.Sanitize(ex, Secrets);

        redacted.Data.Contains("key " + ErrorMapper.Redacted).ShouldBeTrue();
        redacted.Data.Contains("key " + Secret).ShouldBeFalse();
        redacted.Data[NachosExceptionData.RetryAfter].ShouldBe(TimeSpan.FromSeconds(3));
        redacted.Data["Nachos.Other"].ShouldBe("library text mentioning " + Secret);
    }

    [Fact]
    public void LibraryDataAlone_IsNotRewritten()
    {
        var ex = new OperationCanceledException("canceled");
        ex.Data["Nachos.Other"] = Secret;

        SecretRedaction.Sanitize(ex, Secrets).ShouldBeSameAs(ex);
    }

    [Fact]
    public void TaskCanceledException_KeepsItsTypeAndToken()
    {
        using var cts = new CancellationTokenSource();
        var ex = new TaskCanceledException("canceled " + Secret, null, cts.Token);

        var redacted = SecretRedaction.Sanitize(ex, Secrets).ShouldBeOfType<TaskCanceledException>();

        redacted.CancellationToken.ShouldBe(cts.Token);
        redacted.Message.ShouldBe("canceled " + ErrorMapper.Redacted);
    }

    [Fact]
    public void OperationCanceledException_KeepsItsTypeAndToken()
    {
        using var cts = new CancellationTokenSource();
        var ex = new OperationCanceledException("canceled " + Secret, cts.Token);

        var redacted = SecretRedaction.Sanitize(ex, Secrets).ShouldBeOfType<OperationCanceledException>();

        redacted.CancellationToken.ShouldBe(cts.Token);
    }

    [Fact]
    public void KeptConnectionFailure_WithASecret_KeepsItsErrorAndStatus_AndRedactsItsData()
    {
        var ex = new HttpRequestException(HttpRequestError.NameResolutionError, "no such host " + Secret, null, HttpStatusCode.BadGateway);
        ex.Data["echo"] = Secret;

        var redacted = SecretRedaction.Sanitize(ex, Secrets).ShouldBeOfType<HttpRequestException>();

        redacted.HttpRequestError.ShouldBe(HttpRequestError.NameResolutionError);
        redacted.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        redacted.Message.ShouldBe(SecretRedaction.ConnectionMessage(HttpRequestError.NameResolutionError, null));
        redacted.Data["echo"].ShouldBe(ErrorMapper.Redacted);
    }

    [Fact]
    public void CancellationAroundAConnectionFailure_KeepsTypeAndToken_AndGetsTheHostFreeFailureInside()
    {
        using var cts = new CancellationTokenSource();
        var ex = new TaskCanceledException(
            "The operation was canceled.",
            new HttpRequestException(HttpRequestError.ConnectionError, "refused (evil.example:80)", new SocketException(10061)),
            cts.Token);

        var sanitized = SecretRedaction.Sanitize(ex, Secrets).ShouldBeOfType<TaskCanceledException>();

        sanitized.CancellationToken.ShouldBe(cts.Token);
        sanitized.Message.ShouldBe("The operation was canceled.");
        var inner = sanitized.InnerException.ShouldBeOfType<HttpRequestException>();
        inner.Message.ShouldBe(SecretRedaction.ConnectionMessage(HttpRequestError.ConnectionError, SocketError.ConnectionRefused));
        inner.InnerException.ShouldBeOfType<SocketException>();
    }

    [Fact]
    public void SecretFreeInnerException_KeepsItsIdentity_InsideARebuiltChain()
    {
        var inner = new SocketException(10054);
        var ex = new OperationCanceledException("outer " + Secret, inner);

        SecretRedaction.Sanitize(ex, Secrets).InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void RebuiltMessage_IsBounded()
    {
        var ex = new OperationCanceledException(Secret + new string('z', 5000));

        var message = SecretRedaction.Sanitize(ex, Secrets).Message;

        message.Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
        message.ShouldEndWith(ErrorMapper.TruncationMarker);
    }

    [Fact]
    public void OtherKeptTypes_BecomeIOException_NamingTheOriginalType_AndReadingAsIt()
    {
        var redacted = SecretRedaction.Sanitize(new ObjectDisposedException(Secret), Secrets);

        redacted.ShouldBeOfType<IOException>().Message.ShouldStartWith("System.ObjectDisposedException: ");
        redacted.Message.ShouldNotContain(Secret);
        SecretRedaction.ReplacedType(redacted).ShouldBe(typeof(ObjectDisposedException));
    }

    [Fact]
    public void SocketExceptionWithASecretInItsData_BecomesIOException_ReadingAsASocketException()
    {
        var socket = new SocketException(10054);
        socket.Data["echo"] = Secret;

        var redacted = SecretRedaction.Sanitize(socket, Secrets);

        redacted.ShouldBeOfType<IOException>().Data["echo"].ShouldBe(ErrorMapper.Redacted);
        SecretRedaction.ReplacedType(redacted).ShouldBe(typeof(SocketException));
    }

    [Fact]
    public void VeryDeepChain_IsRedactedWithoutOverflow_AndCapped()
    {
        Exception ex = new TimeoutException("leaf " + Secret);
        for (var i = 0; i < 20_000; i++)
        {
            ex = new OperationCanceledException($"level {i} {Secret}", ex);
        }

        var redacted = SecretRedaction.Sanitize(ex, Secrets);

        var depth = 0;
        for (var current = redacted; current is not null; current = current.InnerException)
        {
            current.Message.ShouldNotContain(Secret);
            depth++;
        }

        depth.ShouldBe(32);
    }

    [Fact]
    public void VeryDeepUnsafeChain_IsReplacedWithoutOverflow()
    {
        Exception ex = new IOException("leaf " + Secret);
        for (var i = 0; i < 20_000; i++)
        {
            ex = new OperationCanceledException($"level {i}", ex);
        }

        var sanitized = SecretRedaction.Sanitize(ex, Secrets);

        var depth = 0;
        for (var current = sanitized; current is not null; current = current.InnerException)
        {
            current.Message.ShouldNotContain(Secret);
            depth++;
        }

        depth.ShouldBeLessThanOrEqualTo(32);
    }

    [Fact]
    public async Task SecretInAnAttemptTimeoutCause_NeverSurfaces()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var stub = new LambdaHandler((request, ct) =>
        {
            time.Advance(TimeSpan.FromSeconds(30));
            throw new OperationCanceledException("canceled while sending " + request.Headers.Authorization, ct);
        });
        using var invoker = new HttpMessageInvoker(new RetryHandler(time, (_, _) => Task.CompletedTask, () => 0) { InnerHandler = stub });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://nachos.test/v3/keys");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Secret);
        request.Options.Set(RetryHandler.RouteTemplate, "/v3/keys");

        var ex = await Should.ThrowAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));

        ex.InnerException.ShouldBeOfType<TimeoutException>().InnerException.ShouldNotBeNull();
        for (var current = (Exception?)ex; current is not null; current = current.InnerException)
        {
            current.ToString().ShouldNotContain(Secret);
        }
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
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
        ex.Message.ShouldBe(SecretRedaction.CannedMessage(HttpRequestError.Unknown) + " Retry-After: 40s.");
        ex.InnerException.ShouldBeNull();
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
