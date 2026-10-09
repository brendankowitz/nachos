using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nachos.Abstractions;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// A server (or anything on the path) that echoes the bearer value into a malformed response line makes the
/// transport's own exception text carry it, for example <c>Received an invalid header name: 'Bearer …'</c>. The token
/// or API key must reach neither any exception the client raises (message, data, inner chain) nor the
/// <see cref="IHttpClientFactory"/> logs. Real-socket cases go through the default <c>AddNachosClient</c> pipeline;
/// stub cases replace the primary handler, which leaves exception sanitizing to the client and the retry handler.
/// </summary>
public sealed class TransportRedactionTests
{
    /// <summary>A JWT-shaped canary of 1555 characters (header.payload.signature, base64url alphabet).</summary>
    private static readonly string Token = JwtShaped(1555);

    /// <summary>A 34-character canary API key whose first characters are hex digits, as a JWT's are ("e").</summary>
    private const string ApiKey = "cafe-PROBE-APIKEY-0123456789abcdef";

    public static TheoryData<string, string, string> RealSocketCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var failure in new[]
        {
            "invalid header name", "invalid header line", "invalid trailer after a chunked 200",
            "invalid trailer after a 503 with Retry-After", "bearer where a chunk size belongs (hex-dumped)",
            "bare value where a chunk size belongs (leading hex digits consumed)",
        })
        {
            foreach (var auth in new[] { "credential", "api key" })
            {
                foreach (var route in new[] { "GET message", "POST keys" })
                {
                    data.Add(failure, auth, route);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RealSocketCases))]
    public async Task EchoedBearer_InAMalformedResponse_NeverReachesExceptionsOrLogs(string failure, string auth, string route)
    {
        await using var server = new EchoingServer(authorization => failure switch
        {
            "invalid header name" => $"HTTP/1.1 200 OK\r\n{authorization}: x\r\nContent-Length: 2\r\n\r\n{{}}",
            "invalid header line" => $"HTTP/1.1 200 OK\r\n{authorization}\r\nContent-Length: 2\r\n\r\n{{}}",
            "invalid trailer after a chunked 200" =>
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{{}}\r\n0\r\n{authorization}: x\r\n\r\n",

            // .NET reads "Bea" as a hex chunk size and reports the rest of the line as an invalid chunk extension,
            // hex-dumped: "72-65-72-20-65-79-4A-…".
            "bearer where a chunk size belongs (hex-dumped)" =>
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n{authorization}\r\n{{}}\r\n0\r\n\r\n",

            // A bare JWT starts "eyJ" and the key "cafe": .NET takes the leading hex digits as the chunk size and dumps only
            // the rest, so no form of the whole value appears; only the fixed text keeps it out.
            "bare value where a chunk size belongs (leading hex digits consumed)" =>
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n{Bare(authorization)}\r\n{{}}\r\n0\r\n\r\n",
            _ => "HTTP/1.1 503 Service Unavailable\r\nRetry-After: 1\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n" +
                 $"2\r\n{{}}\r\n0\r\n{authorization}: x\r\n\r\n",
        });
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, server.BaseAddress, auth));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<INachosClient>();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Call(client, route));

        server.Requests.ShouldBe(route == "GET message" ? RetryHandler.MaxAttempts : 1);
        server.Authorizations.ShouldAllBe(a => a.EndsWith(Secret(auth), StringComparison.Ordinal));
        AssertNoSecret(ex, auth);
        ex.Message.ShouldStartWith("The Nachos HTTP exchange failed (");
        ex.InnerException.ShouldBeNull();
        AssertNoSecretText(logs.Text, "logs");
        if (failure.Contains("503", StringComparison.Ordinal) && route == "GET message")
        {
            ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
            delay.ShouldBe(TimeSpan.FromSeconds(1));
            ex.Message.ShouldEndWith(" Retry-After: 1s.");
        }
    }

    [Fact]
    public async Task ApiKeyWithCommaAndQuote_IsRedactedOnEveryAttempt_InTheLogs()
    {
        // Retry copies carry the header unvalidated; a key with ',' or '"' must still be recognised on each attempt.
        const string key = "nk-CANARY,\"quoted\"-key-77";
        await using var server = new EchoingServer(authorization => $"HTTP/1.1 200 OK\r\n{authorization}\r\nContent-Length: 2\r\n\r\n{{}}");
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o =>
        {
            o.BaseAddress = server.BaseAddress;
            o.ApiKey = key;
        });
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        server.Requests.ShouldBe(RetryHandler.MaxAttempts);
        server.Authorizations.ShouldAllBe(a => a == "Bearer " + key);
        logs.Text.ShouldContain("is withheld because it can repeat what the server sent");
        logs.Text.ShouldNotContain(key);
        ex.ToString().ShouldNotContain(key);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("api key")]
    public async Task LongEchoedTrailer_After503_KeepsTheRetryAfterSuffixAndStatus(string auth)
    {
        await using var server = new EchoingServer(authorization =>
            "HTTP/1.1 503 Service Unavailable\r\nRetry-After: 1\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n" +
            $"2\r\n{{}}\r\n0\r\n{authorization}{new string('z', 2500)}: x\r\n\r\n");
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, server.BaseAddress, auth));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        server.Requests.ShouldBe(RetryHandler.MaxAttempts);
        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromSeconds(1));
        ex.Message.ShouldEndWith(" Retry-After: 1s.");
        ex.Message.Length.ShouldBeLessThanOrEqualTo(ErrorMapper.MaxMessageLength);
        AssertNoSecret(ex, auth);
    }

    private const string Chunked = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n";

    private const string Chunked503 =
        "HTTP/1.1 503 Service Unavailable\r\nRetry-After: 1\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n";

    /// <summary>Echo shapes: A is the whole Authorization value ("Bearer …"), T the bare token; '\u0001' splits TCP writes.</summary>
    private static readonly Dictionary<string, Func<string, string, string>> EchoShapes = new()
    {
        ["status line is the bearer"] = (a, _) => $"{a}\r\n\r\n",
        ["status line is the bare token"] = (_, t) => $"{t}\r\n\r\n",
        ["token as the status code"] = (_, t) => $"HTTP/1.1 {t}\r\nContent-Length: 0\r\n\r\n",
        ["token as the reason phrase of a 404"] = (_, t) => $"HTTP/1.1 404 {t}\r\nContent-Length: 0\r\n\r\n",
        ["token as the reason phrase of a 503 with Retry-After"] = (_, t) => $"HTTP/1.1 503 {t}\r\nRetry-After: 1\r\nContent-Length: 0\r\n\r\n",
        ["bearer as a header name"] = (a, _) => $"HTTP/1.1 200 OK\r\n{a}: x\r\nContent-Length: 2\r\n\r\n{{}}",
        ["bare token as a (valid) header name"] = (_, t) => $"HTTP/1.1 200 OK\r\n{t}: x\r\nContent-Length: 2\r\n\r\n{{}}",
        ["bearer as a header line"] = (a, _) => $"HTTP/1.1 200 OK\r\n{a}\r\nContent-Length: 2\r\n\r\n{{}}",
        ["bare token as a header line"] = (_, t) => $"HTTP/1.1 200 OK\r\n{t}\r\nContent-Length: 2\r\n\r\n{{}}",
        ["bearer as a header value"] = (a, _) => $"HTTP/1.1 200 OK\r\nX-Echo: {a}\r\nContent-Length: 2\r\n\r\n{{}}",
        ["bearer as the Content-Length"] = (a, _) => $"HTTP/1.1 200 OK\r\nContent-Length: {a}\r\n\r\n{{}}",
        ["bare token as the Content-Type"] = (_, t) => $"HTTP/1.1 200 OK\r\nContent-Type: {t}\r\nContent-Length: 2\r\n\r\n{{}}",
        ["bare token as the Retry-After of a 503"] = (_, t) => $"HTTP/1.1 503 Service Unavailable\r\nRetry-After: {t}\r\nContent-Length: 0\r\n\r\n",
        ["bearer as a chunk size"] = (a, _) => Chunked + $"{a}\r\n{{}}\r\n0\r\n\r\n",
        ["bare token as a chunk size"] = (_, t) => Chunked + $"{t}\r\n{{}}\r\n0\r\n\r\n",
        ["bare token as a chunk extension"] = (_, t) => Chunked + $"2;{t}\r\n{{}}\r\n0\r\n\r\n",
        ["bearer as a chunk extension"] = (a, _) => Chunked + $"2;{a}\r\n{{}}\r\n0\r\n\r\n",
        ["bare token overrunning a chunk"] = (_, t) => Chunked + $"2\r\n{t}\r\n0\r\n\r\n",
        ["bearer as a trailer name"] = (a, _) => Chunked + $"2\r\n{{}}\r\n0\r\n{a}: x\r\n\r\n",
        ["bare token as a trailer name"] = (_, t) => Chunked + $"2\r\n{{}}\r\n0\r\n{t}: x\r\n\r\n",
        ["bare token as a trailer line"] = (_, t) => Chunked + $"2\r\n{{}}\r\n0\r\n{t}\r\n\r\n",
        ["bearer as a trailer value"] = (a, _) => Chunked + $"2\r\n{{}}\r\n0\r\nX-T: {a}\r\n\r\n",
        ["503 with Retry-After, bare token as a chunk size"] = (_, t) => Chunked503 + $"{t}\r\n{{}}\r\n0\r\n\r\n",
        ["503 with Retry-After, bearer as a trailer name"] = (a, _) => Chunked503 + $"2\r\n{{}}\r\n0\r\n{a}: x\r\n\r\n",
        ["503 with Retry-After, bearer as a header name"] = (a, _) => $"HTTP/1.1 503 Service Unavailable\r\nRetry-After: 1\r\n{a}: x\r\nContent-Length: 0\r\n\r\n",
        ["LF-only lines, bare token as a chunk size"] = (_, t) => $"HTTP/1.1 200 OK\nTransfer-Encoding: chunked\n\n{t}\n{{}}\n0\n\n",
        ["LF-only lines, bearer as a header name"] = (a, _) => $"HTTP/1.1 200 OK\n{a}: x\nContent-Length: 2\n\n{{}}",
        ["bare CR inside a header line"] = (a, _) => $"HTTP/1.1 200 OK\r\nX: {a}\rY: z\r\nContent-Length: 2\r\n\r\n{{}}",
        ["split write inside the token, bare chunk size"] = (_, t) => Chunked + $"{t[..10]}\u0001{t[10..]}\r\n{{}}\r\n0\r\n\r\n",
        ["split write inside the token, bearer header name"] = (a, _) => $"HTTP/1.1 200 OK\r\n{a[..20]}\u0001{a[20..]}: x\r\nContent-Length: 2\r\n\r\n{{}}",
        ["split write before a bare trailer line"] = (_, t) => Chunked + $"2\r\n{{}}\r\n0\r\n\u0001{t}\r\n\r\n",
    };

    public static TheoryData<string, string> FuzzCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var shape in EchoShapes.Keys)
        {
            data.Add(shape, "GET message");
            data.Add(shape, "POST keys");
        }

        return data;
    }

    /// <summary>
    /// Whatever shape the echo takes, no 12-character piece of the token (plain, or inside any decoded hex run) reaches
    /// the exception chain or the logs.
    /// </summary>
    [Theory]
    [MemberData(nameof(FuzzCases))]
    public async Task EchoedToken_InAnyShape_NeverSurfaces(string shape, string route)
    {
        await using var server = new EchoingServer(authorization => EchoShapes[shape](authorization, Bare(authorization)));
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, server.BaseAddress, "credential"));
        await using var provider = services.BuildServiceProvider();

        var text = new StringBuilder();
        try
        {
            await Call(provider.GetRequiredService<INachosClient>(), route);
        }
        catch (Exception ex)
        {
            text.AppendLine(ex.ToString());
            foreach (var current in Chain(ex))
            {
                text.AppendLine(current.Message).AppendLine(current.ToString());
                foreach (DictionaryEntry entry in current.Data)
                {
                    text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{entry.Key}={entry.Value}");
                }
            }
        }

        server.Requests.ShouldBeGreaterThan(0);
        SecretScan.FindLeak(text.ToString(), Token, window: 12).ShouldBeNull("the exception chain leaks the token");
        SecretScan.FindLeak(logs.Text, Token, window: 12).ShouldBeNull("the logs leak the token");
    }

    [Fact]
    public void SecretScan_FindsPiecesAndHexTails()
    {
        var token = Token;
        SecretScan.FindLeak("x" + token[100..112] + "x", token, 12).ShouldNotBeNull();
        SecretScan.FindLeak(BitConverter.ToString(Encoding.ASCII.GetBytes(token[1..])), token, 12).ShouldNotBeNull();
        SecretScan.FindLeak(Convert.ToHexString(Encoding.ASCII.GetBytes(token[7..40])).ToLowerInvariant(), token, 12).ShouldNotBeNull();
        SecretScan.FindLeak("0" + Convert.ToHexString(Encoding.ASCII.GetBytes(token[7..40])), token, 12).ShouldNotBeNull();
        SecretScan.FindLeak(token[100..111], token, 12).ShouldBeNull();
        SecretScan.FindLeak("unrelated text 12-34-56", token, 12).ShouldBeNull();
    }

    public static TheoryData<string, string> StubCases() => new()
    {
        { "credential", "GET message" },
        { "credential", "POST keys" },
        { "api key", "GET message" },
        { "api key", "POST keys" },
    };

    [Theory]
    [MemberData(nameof(StubCases))]
    public async Task TransportFailureText_WithTheBearer_IsSanitized_WhenThePrimaryHandlerIsReplaced(string auth, string route)
    {
        var secret = Secret(auth);
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, new Uri("https://nachos.test/"), auth))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, _) =>
            {
                Interlocked.Increment(ref attempts);
                var failure = new HttpRequestException(
                    HttpRequestError.InvalidResponse,
                    $"Received an invalid header name: '{request.Authorization}'.",
                    new AggregateException(new IOException($"inner echo {secret}"), new SocketException(10054)));
                failure.Data["echo"] = $"data {secret}";
                throw failure;
            }));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Call(provider.GetRequiredService<INachosClient>(), route));

        attempts.ShouldBe(route == "GET message" ? RetryHandler.MaxAttempts : 1);
        ex.HttpRequestError.ShouldBe(HttpRequestError.InvalidResponse);
        ex.Message.ShouldBe(SecretRedaction.CannedMessage(HttpRequestError.InvalidResponse));
        ex.InnerException.ShouldBeNull();
        AssertNoSecret(ex, auth);
    }

    [Fact]
    public async Task CallerCancellation_WithAnEchoingTransportError_IsSanitizedToo()
    {
        // HttpClient reports a failure after the caller cancelled as a cancellation whose inner exception is the raw
        // transport failure; that inner text must not carry the token either.
        using var cts = new CancellationTokenSource();
        var services = new ServiceCollection();
        services.AddNachosClient(o => Configure(o, new Uri("https://nachos.test/"), "credential"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, _) =>
            {
                cts.Cancel();
                throw new HttpRequestException($"socket closed while sending '{request.Authorization}'");
            }));
        await using var provider = services.BuildServiceProvider();

        Exception? caught = null;
        try
        {
            await provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1", cts.Token);
        }
        catch (Exception e)
        {
            caught = e;
        }

        var cancelled = caught.ShouldBeAssignableTo<OperationCanceledException>()!;
        cancelled.CancellationToken.ShouldBe(cts.Token);
        AssertNoSecret(cancelled, "credential");
    }

    [Fact]
    public async Task ConnectionFailure_WithoutTheBearer_IsRebuiltWithoutTheHost_KeepingItsSocketException()
    {
        var socket = new SocketException(10061);
        var original = new HttpRequestException(HttpRequestError.ConnectionError, "connection refused (nachos.test:443)", socket);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, new Uri("https://nachos.test/"), "api key"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((_, _) => throw original));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().CreateKeyAsync("w"));

        ex.HttpRequestError.ShouldBe(HttpRequestError.ConnectionError);
        ex.Message.ShouldBe(SecretRedaction.ConnectionMessage(HttpRequestError.ConnectionError, SocketError.ConnectionRefused));
        ex.Message.ShouldNotContain("nachos.test");
        ex.InnerException.ShouldBeSameAs(socket);
    }

    /// <summary>
    /// A <c>Location</c> whose hostname spells the bearer value in hex, split into DNS labels. Following it would make
    /// the redirected hop's name-resolution failure name that host, so the default pipeline does not follow: the 3xx is
    /// final under spec §16 status precedence and surfaces as the mapped exception, once.
    /// </summary>
    [Theory]
    [MemberData(nameof(StubCases))]
    public async Task RedirectToAHostSpellingTheBearer_IsNotFollowed_AndSurfacesAsTheFinalStatus(string auth, string route)
    {
        await using var server = new EchoingServer(authorization =>
            $"HTTP/1.1 302 Found\r\nLocation: http://{HexHost(Bare(authorization))}/\r\nContent-Length: 0\r\n\r\n");
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, server.BaseAddress, auth));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Call(provider.GetRequiredService<INachosClient>(), route));

        server.Requests.ShouldBe(1);
        ex.StatusCode.ShouldBe(HttpStatusCode.Found);
        ex.Message.ShouldContain("returned 302");
        ex.InnerException.ShouldBeNull();
        AssertNoSecretWindow(ex, logs.Text, Secret(auth));
    }

    /// <summary>
    /// A primary handler of the caller's own that follows redirects: the redirected hop fails to resolve the hex
    /// hostname (through the connect callback, so no DNS query is made), and .NET's own text names that host. The kept
    /// connection failure is rebuilt without it, so neither the exception chain nor the <c>LogicalHandler</c> log
    /// carries the value. The <c>ClientHandler</c> log sits between that handler and the retry handler and does (as
    /// documented on <c>AddNachosClient</c>): only the default pipeline's wrapper covers it.
    /// </summary>
    [Theory]
    [MemberData(nameof(StubCases))]
    public async Task RedirectFollowedByACallersHandler_SurfacesTheFailure_WithoutTheHost(string auth, string route)
    {
        await using var server = new EchoingServer(authorization =>
            $"HTTP/1.1 302 Found\r\nLocation: http://{HexHost(Bare(authorization))}/\r\nContent-Length: 0\r\n\r\n");
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, server.BaseAddress, auth))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = async (context, ct) =>
                {
                    if (context.DnsEndPoint.Host != server.BaseAddress.Host)
                    {
                        throw new SocketException((int)SocketError.HostNotFound);
                    }

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            });
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Call(provider.GetRequiredService<INachosClient>(), route));

        server.Requests.ShouldBe(route == "GET message" ? RetryHandler.MaxAttempts : 1);
        ex.HttpRequestError.ShouldBe(HttpRequestError.NameResolutionError);
        ex.Message.ShouldBe(SecretRedaction.ConnectionMessage(HttpRequestError.NameResolutionError, SocketError.HostNotFound));
        ex.InnerException.ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.HostNotFound);
        AssertNoSecretWindow(ex, logs.TextOf(".LogicalHandler"), Secret(auth));
        logs.TextOf(".LogicalHandler").ShouldContain("HTTP request failed");

        // The raw .NET text of the redirected hop does name the host: that log is the caller's handler's to cover.
        SecretScan.FindLeak(logs.TextOf(".ClientHandler"), Secret(auth), window: 12).ShouldNotBeNull();
    }

    [Fact]
    public void HexHost_SpellsTheSecret_AndTheScanSeesThroughTheDots()
    {
        var host = HexHost(ApiKey);

        host.ShouldBe("636166652d50524f42452d4150494b45592d303132333435363738396162.63646566");
        SecretScan.FindLeak($"Name or service not known ({host}:80)", ApiKey, window: 12).ShouldNotBeNull();
        SecretScan.FindLeak($"x ({host[..30]}.{host[30..]}:80)", ApiKey, window: 12).ShouldNotBeNull();
    }

    /// <summary>The hex of <paramref name="secret"/>'s bytes, lowercased, as DNS labels of at most 60 characters.</summary>
    private static string HexHost(string secret)
    {
        var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(secret)).ToLowerInvariant();
        return string.Join('.', Enumerable.Range(0, (hex.Length + 59) / 60).Select(i => hex.Substring(i * 60, Math.Min(60, hex.Length - i * 60))));
    }

    /// <summary>No 12-character window of <paramref name="secret"/>, plain or in any hex form, in the chain or the logs.</summary>
    private static void AssertNoSecretWindow(Exception ex, string logs, string secret)
    {
        var text = new StringBuilder(ex.ToString());
        foreach (var current in Chain(ex))
        {
            text.AppendLine(current.Message).AppendLine(current.ToString());
            foreach (DictionaryEntry entry in current.Data)
            {
                text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{entry.Key}={entry.Value}");
            }
        }

        SecretScan.FindLeak(text.ToString(), secret, window: 12).ShouldBeNull("the exception chain leaks the secret");
        SecretScan.FindLeak(logs, secret, window: 12).ShouldBeNull("the logs leak the secret");
    }

    private static void Configure(NachosClientOptions options, Uri baseAddress, string auth)
    {
        options.BaseAddress = baseAddress;
        options.ApiKey = ApiKey;
        if (auth == "credential")
        {
            options.Credential = new StaticCredential(Token);
            options.Scopes = ["api://nachos/.default"];
        }
    }

    private static string Secret(string auth) => auth == "credential" ? Token : ApiKey;

    private static Task Call(INachosClient client, string route) =>
        route == "GET message" ? client.GetMessageAsync("w1", "s1", "m1") : client.CreateKeyAsync("w1");

    private static IEnumerable<Exception> Chain(Exception root)
    {
        var pending = new Stack<Exception>([root]);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    private static void AssertNoSecret(Exception ex, string auth)
    {
        Secret(auth).ShouldNotBeEmpty();
        AssertNoSecretText(ex.ToString(), "ToString");
        foreach (var current in Chain(ex))
        {
            AssertNoSecretText(current.Message, current.GetType().Name + ".Message");
            AssertNoSecretText(current.ToString(), current.GetType().Name + ".ToString");
            foreach (DictionaryEntry entry in current.Data)
            {
                AssertNoSecretText($"{entry.Key}={entry.Value}", "Data");
            }
        }
    }

    /// <summary>
    /// No 20-character window of either canary (so no 20+-character tail either) appears in <paramref name="text"/>,
    /// as plain text or inside any hex run of it decoded to bytes.
    /// </summary>
    private static void AssertNoSecretText(string text, string where)
    {
        foreach (var secret in new[] { Token, ApiKey })
        {
            SecretScan.FindLeak(text, secret, window: 20).ShouldBeNull($"{where} leaks a canary");
        }
    }

    private static string Bare(string authorization) =>
        authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization["Bearer ".Length..] : authorization;

    private static string JwtShaped(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var text = new StringBuilder("eyJhbGciOiJSUzI1NiIsImtpZCI6IkNBTkFSWSJ9.");
        for (var i = 0; text.Length < length - 44; i++)
        {
            text.Append(alphabet[(i * 7 + 3) % alphabet.Length]);
        }

        text.Append(".CANARYsig");
        while (text.Length < length)
        {
            text.Append(alphabet[text.Length % alphabet.Length]);
        }

        return text.ToString();
    }

    private sealed class StaticCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    /// <summary>
    /// A loopback HTTP/1.1 server that reads each request's head and body, then writes the response built from the
    /// request's <c>Authorization</c> value and closes the connection.
    /// </summary>
    private sealed class EchoingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, string> _respond;
        private readonly List<string> _authorizations = [];
        private readonly Task _loop;

        public EchoingServer(Func<string, string> respond)
        {
            _respond = respond;
            _listener.Start();
            BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _loop = Task.Run(AcceptAsync);
        }

        public Uri BaseAddress { get; }

        public int Requests
        {
            get
            {
                lock (_authorizations)
                {
                    return _authorizations.Count;
                }
            }
        }

        public IReadOnlyList<string> Authorizations
        {
            get
            {
                lock (_authorizations)
                {
                    return [.. _authorizations];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = connection.GetStream();
                var head = await ReadHeadAsync(stream);
                var lines = head.Split("\r\n");
                var authorization = lines.FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?[14..].Trim() ?? "";
                var length = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } header
                    ? int.Parse(header[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                await stream.ReadExactlyAsync(new byte[length], _stop.Token);
                lock (_authorizations)
                {
                    _authorizations.Add(authorization);
                }

                // '\u0001' marks a split: the parts go out as separate TCP writes, a little apart.
                var parts = _respond(authorization).Split('\u0001');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        await Task.Delay(20, _stop.Token);
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(parts[i]), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
                connection.Client.Shutdown(SocketShutdown.Send);
            }
        }

        private async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                {
                    break;
                }

                head.Append((char)one[0]);
            }

            return head.ToString();
        }
    }
}
