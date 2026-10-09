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
        AddLoggedNachosClient(services, o => Configure(o, server.BaseAddress, auth));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<INachosClient>();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Call(client, route));

        server.Requests.ShouldBe(route == "GET message" ? RetryHandler.MaxAttempts : 1);
        server.Authorizations.ShouldAllBe(a => a.EndsWith(Secret(auth), StringComparison.Ordinal));
        AssertNoSecret(ex, auth);
        ex.InnerException.ShouldBeNull();
        AssertNoSecretText(logs.FormattedText, "logs");

        // The whole text is fixed: the static sentence names the error, then the Retry-After suffix when the retry handler
        // wrapped a body failure after a 503 (a never-retried route's body fails inside HttpClient, without the header).
        var surfacedRetryAfter = failure.Contains("503", StringComparison.Ordinal) && route == "GET message";
        ex.HttpRequestError.ShouldBe(HttpRequestError.InvalidResponse);
        ex.Message.ShouldBe(
            "The Nachos HTTP exchange failed (InvalidResponse). The transport's own description is withheld because it can repeat what the server sent." +
            (surfacedRetryAfter ? " Retry-After: 1s." : string.Empty));
        if (surfacedRetryAfter)
        {
            ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
            delay.ShouldBe(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Response shapes that carry the request's bearer value in a header VALUE, or carry nothing unusual.</summary>
    private static readonly string[] HeaderValueShapes =
    [
        "well-behaved 200", "Location on a 302", "WWW-Authenticate on a 401", "Retry-After on a 503",
        "Content-Type charset on a 200", "Content-Length on a 200", "X-Echo on a 200",
    ];

    public static TheoryData<string, string, string> HeaderValueCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var shape in HeaderValueShapes)
        {
            foreach (var auth in new[] { "credential", "api key" })
            {
                foreach (var route in new[] { "GET message", "POST keys" })
                {
                    data.Add(shape, auth, route);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// The factory's built-in <c>LogicalHandler</c> and <c>ClientHandler</c> loggers write every request and response
    /// header at Trace; only their formatted text replaces values with <c>*</c>, while the structured state a provider
    /// such as OpenTelemetry exports carries the raw values: the request's own <c>Authorization</c> on every call, and
    /// whatever a server echoes into <c>Location</c>, <c>WWW-Authenticate</c>, <c>Retry-After</c>, a charset or any
    /// other header value. <c>AddNachosClient</c> removes those loggers, so with a capture provider at Trace neither
    /// the structured state nor the formatted text carries the value, whatever the server sends.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeaderValueCases))]
    public async Task HeaderValues_NeverReachTheLogs_StructuredStateIncluded(string shape, string auth, string route)
    {
        var body = route == "GET message" ? MessageJson : """{"key":"nk-created"}""";
        var ok = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}";
        await using var server = new EchoingServer(authorization => shape switch
        {
            "well-behaved 200" => ok,
            "Location on a 302" => $"HTTP/1.1 302 Found\r\nLocation: http://example.invalid/?t={Bare(authorization)}\r\nContent-Length: 0\r\n\r\n",
            "WWW-Authenticate on a 401" => $"HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: {authorization}\r\nContent-Length: 0\r\n\r\n",
            "Retry-After on a 503" => $"HTTP/1.1 503 Service Unavailable\r\nRetry-After: {Bare(authorization)}\r\nContent-Length: 0\r\n\r\n",
            "Content-Type charset on a 200" =>
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset={Bare(authorization)}\r\nContent-Length: {body.Length}\r\n\r\n{body}",
            "Content-Length on a 200" => $"HTTP/1.1 200 OK\r\nContent-Length: {Bare(authorization)}\r\n\r\n{{}}",
            _ => $"HTTP/1.1 200 OK\r\nX-Echo: {authorization}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}",
        });
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, server.BaseAddress, auth));
        await using var provider = services.BuildServiceProvider();

        Exception? failure = null;
        try
        {
            await Call(provider.GetRequiredService<INachosClient>(), route);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        server.Requests.ShouldBeGreaterThan(0);
        server.Authorizations.ShouldAllBe(a => a == "Bearer " + Secret(auth));
        if (failure is not null)
        {
            AssertNoSecretWindow(failure, logs.Text, Secret(auth));
        }

        SecretScan.FindLeak(logs.Text, Secret(auth), window: 12).ShouldBeNull("the structured log state leaks the bearer value");
        SecretScan.FindLeak(logs.FormattedText, Secret(auth), window: 12).ShouldBeNull("the formatted log text leaks the bearer value");
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
        AddLoggedNachosClient(services, o =>
        {
            o.BaseAddress = server.BaseAddress;
            o.ApiKey = key;
        });
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        server.Requests.ShouldBe(RetryHandler.MaxAttempts);
        server.Authorizations.ShouldAllBe(a => a == "Bearer " + key);
        logs.FormattedText.ShouldContain("is withheld because it can repeat what the server sent");
        logs.FormattedText.ShouldNotContain(key);
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
        AddLoggedNachosClient(services, o => Configure(o, server.BaseAddress, auth));
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
        AddLoggedNachosClient(services, o => Configure(o, server.BaseAddress, "credential"));
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
        SecretScan.FindLeak(logs.FormattedText, Token, window: 12).ShouldBeNull("the logs leak the token");
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

    public static TheoryData<string, string, string, bool> ReplacedPrimaryCases()
    {
        var data = new TheoryData<string, string, string, bool>();
        foreach (var (auth, route) in new[] { ("credential", "GET message"), ("credential", "POST keys"), ("api key", "GET message"), ("api key", "POST keys") })
        {
            foreach (var wrapped in new[] { true, false })
            {
                data.Add("invalid header name (replaced by fixed text)", auth, route, wrapped);
                data.Add("connection failure over a timeout naming the bearer (kept, redacted)", auth, route, wrapped);
            }
        }

        return data;
    }

    /// <summary>
    /// A primary handler of the caller's own is wrapped like the default one, so the <c>ClientHandler</c> log below
    /// the retry handler is clean too. With the wrapper taken out (what <see cref="RetryHandler"/> alone guarantees for
    /// anything thrown between the primary handler and itself, a handler the caller adds for example) the raw failure
    /// does reach that log, but what leaves <see cref="RetryHandler"/> must already be clean, whatever
    /// <see cref="NachosHttpClient"/> does afterwards: the <c>LogicalHandler</c> log above it sees exactly that (the
    /// factory logs an <see cref="HttpRequestException"/> with its whole chain), for a failure that is replaced and
    /// for a kept chain whose inner level is only redacted.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReplacedPrimaryCases))]
    public async Task TransportFailureText_WithTheBearer_IsSanitized_WhenThePrimaryHandlerIsReplaced(string failure, string auth, string route, bool wrapped)
    {
        var secret = Secret(auth);
        var replaced = failure.StartsWith("invalid", StringComparison.Ordinal);
        var attempts = 0;
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        AddLoggedNachosClient(services, o => Configure(o, new Uri("https://nachos.test/"), auth))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, _) =>
            {
                Interlocked.Increment(ref attempts);
                if (!replaced)
                {
                    throw new HttpRequestException(
                        HttpRequestError.ConnectionError,
                        "connect failed (nachos.test:443)",
                        new TimeoutException($"timed out sending '{request.Authorization}'"));
                }

                var echo = new HttpRequestException(
                    HttpRequestError.InvalidResponse,
                    $"Received an invalid header name: '{request.Authorization}'.",
                    new AggregateException(new IOException($"inner echo {secret}"), new SocketException(10054)));
                echo.Data["echo"] = $"data {secret}";
                throw echo;
            }));
        if (!wrapped)
        {
            RemoveTheWrapper(services);
        }

        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Call(provider.GetRequiredService<INachosClient>(), route));

        attempts.ShouldBe(route == "GET message" ? RetryHandler.MaxAttempts : 1);
        if (replaced)
        {
            ex.HttpRequestError.ShouldBe(HttpRequestError.InvalidResponse);
            ex.Message.ShouldBe(SecretRedaction.CannedMessage(HttpRequestError.InvalidResponse));
            ex.InnerException.ShouldBeNull();
        }
        else
        {
            ex.HttpRequestError.ShouldBe(HttpRequestError.ConnectionError);
            ex.Message.ShouldBe(SecretRedaction.ConnectionMessage(HttpRequestError.ConnectionError, null));
            ex.InnerException.ShouldBeOfType<TimeoutException>().Message.ShouldBe($"timed out sending 'Bearer {ErrorMapper.Redacted}'");
        }

        AssertNoSecret(ex, auth);
        var logical = logs.FormattedTextOf(".LogicalHandler");
        logical.ShouldContain("HTTP request failed");
        AssertNoSecretText(logical, "LogicalHandler logs");
        var clientHandler = logs.FormattedTextOf(".ClientHandler");
        clientHandler.ShouldContain("HTTP request failed");
        if (wrapped)
        {
            SecretScan.FindLeak(clientHandler, secret, window: 12).ShouldBeNull("the wrapper sits below the ClientHandler logger");
        }
        else
        {
            SecretScan.FindLeak(clientHandler, secret, window: 20).ShouldNotBeNull("without the wrapper the ClientHandler log is not covered");
        }
    }

    public static TheoryData<string, bool, int> ClassificationCases() => new()
    {
        { "invalid operation", false, 1 },
        { "invalid operation", true, 1 },
        { "authentication", false, 1 },
        { "authentication", true, 1 },
        { "http invalid response", false, RetryHandler.MaxAttempts },
        { "http invalid response", true, RetryHandler.MaxAttempts },
        { "io", false, RetryHandler.MaxAttempts },
        { "io", true, RetryHandler.MaxAttempts },
    };

    /// <summary>
    /// The wrapper replaces a non-transport failure (a handler's <see cref="InvalidOperationException"/>, say) by an
    /// <see cref="HttpRequestException"/> with fixed text, which the retry handler would otherwise take for a transient
    /// failure and resend. The retry classification follows the original type: the same number of primary calls with
    /// and without the wrapper, and the caller still gets fixed text.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public async Task ReplacedFailure_KeepsTheOriginalRetryClassification_WithAndWithoutTheWrapper(string kind, bool wrapped, int expectedAttempts)
    {
        var attempts = 0;
        var stub = new StubHandler((request, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw kind switch
            {
                "invalid operation" => new InvalidOperationException($"a bug below the retry handler saw '{request.Authorization}'"),
                "authentication" => new System.Security.Authentication.AuthenticationException($"handshake failed for '{request.Authorization}'"),
                "http invalid response" => new HttpRequestException(HttpRequestError.InvalidResponse, $"bad line '{request.Authorization}'"),
                _ => new IOException($"reset '{request.Authorization}'"),
            };
        });
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        AddLoggedNachosClient(services, o => Configure(o, new Uri("https://nachos.test/"), "api key"))
            .ConfigurePrimaryHttpMessageHandler(() => stub);
        if (!wrapped)
        {
            RemoveTheWrapper(services);
        }

        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        attempts.ShouldBe(expectedAttempts);
        ex.Message.ShouldBe(SecretRedaction.CannedMessage(kind == "http invalid response" ? HttpRequestError.InvalidResponse : HttpRequestError.Unknown));
        ex.InnerException.ShouldBeNull();
        AssertNoSecret(ex, "api key");
    }

    public static TheoryData<string, bool> KeptNonTransientCases() => new()
    {
        { "object disposed", false },
        { "object disposed", true },
        { "socket", false },
        { "socket", true },
    };

    /// <summary>
    /// A kept type that mentions the bearer is rebuilt as an <see cref="IOException"/>, which the retry handler would
    /// take for a transient failure; the rebuilt one reads as the original type, so an
    /// <see cref="ObjectDisposedException"/> or a <see cref="SocketException"/> is sent once with and without the
    /// wrapper, as the raw one is.
    /// </summary>
    [Theory]
    [MemberData(nameof(KeptNonTransientCases))]
    public async Task KeptFailureOfANonTransientType_MentioningTheBearer_IsSentOnce_WithAndWithoutTheWrapper(string kind, bool wrapped)
    {
        var attempts = 0;
        var stub = new StubHandler((request, _) =>
        {
            Interlocked.Increment(ref attempts);
            // The constructor's object name is the message .NET prints: a stream named after what it was sending.
            Exception disposed = new ObjectDisposedException($"stream of '{request.Authorization}'");
            if (kind == "object disposed")
            {
                throw disposed;
            }

            var socket = new SocketException((int)SocketError.ConnectionReset);
            socket.Data["echo"] = request.Authorization;
            throw socket;
        });
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        AddLoggedNachosClient(services, o => Configure(o, new Uri("https://nachos.test/"), "api key"))
            .ConfigurePrimaryHttpMessageHandler(() => stub);
        if (!wrapped)
        {
            RemoveTheWrapper(services);
        }

        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<IOException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        attempts.ShouldBe(1);
        SecretRedaction.ReplacedType(ex).ShouldBe(kind == "object disposed" ? typeof(ObjectDisposedException) : typeof(SocketException));
        AssertNoSecret(ex, "api key");
    }

    /// <summary>
    /// The wrapper redacts the request's own bearer value from a kept chain (a connection failure over a timeout
    /// naming it, say) before the <c>ClientHandler</c> logger sees it; with no secrets to hand it would rebuild the
    /// connection failure host-free but pass the raw timeout text through. (The factory logs an
    /// <see cref="HttpRequestException"/> with its whole chain; a bare timeout is not logged.)
    /// </summary>
    [Theory]
    [InlineData("credential")]
    [InlineData("api key")]
    public async Task KeptFailure_MentioningTheBearer_IsRedactedBeforeTheClientHandlerLog(string auth)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        AddLoggedNachosClient(services, o => Configure(o, new Uri("https://nachos.test/"), auth))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, _) =>
                throw new HttpRequestException(
                    HttpRequestError.ConnectionError,
                    "connect failed (nachos.test:443)",
                    new TimeoutException($"timed out sending '{request.Authorization}'"))));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().CreateKeyAsync("w1"));

        var clientHandler = logs.FormattedTextOf(".ClientHandler");
        clientHandler.ShouldContain("HTTP request failed");
        clientHandler.ShouldContain($"timed out sending 'Bearer {ErrorMapper.Redacted}'");
        AssertNoSecretWindow(ex, logs.FormattedText, Secret(auth));
    }

    /// <summary>
    /// The pipeline without <see cref="TransportRedactionHandler"/>: the filter that wraps the primary handler is
    /// taken out, so a stub primary handler's failures reach the <c>ClientHandler</c> logger raw. That is what a handler
    /// the caller adds sees of its own failures, and what <see cref="RetryHandler"/>'s own sanitizing protects.
    /// </summary>
    private static void RemoveTheWrapper(ServiceCollection services) =>
        services.Remove(services.Single(d => d.ImplementationType == typeof(TransportRedactionFilter))).ShouldBeTrue();

    [Fact]
    public async Task CallerCancellation_WithAnEchoingTransportError_IsSanitizedToo()
    {
        // HttpClient reports a failure after the caller cancelled as a cancellation whose inner exception is the raw
        // transport failure; that inner text must not carry the token either.
        using var cts = new CancellationTokenSource();
        var services = new ServiceCollection();
        AddLoggedNachosClient(services, o => Configure(o, new Uri("https://nachos.test/"), "credential"))
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
        AddLoggedNachosClient(services, o => Configure(o, new Uri("https://nachos.test/"), "api key"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((_, _) => throw original));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().CreateKeyAsync("w"));

        ex.HttpRequestError.ShouldBe(HttpRequestError.ConnectionError);
        ex.Message.ShouldBe(SecretRedaction.ConnectionMessage(HttpRequestError.ConnectionError, SocketError.ConnectionRefused));
        ex.Message.ShouldNotContain("nachos.test");
        ex.InnerException.ShouldBeSameAs(socket);
    }

    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","metadata":{},"created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    /// <summary>The ways a caller configures the primary handler that leave a <see cref="SocketsHttpHandler"/> in place.</summary>
    private static readonly string[] SocketsPrimaries =
    [
        "default",
        "UseSocketsHttpHandler turning redirects on",
        "ConfigurePrimaryHttpMessageHandler with a SocketsHttpHandler following redirects",
    ];

    public static TheoryData<string> SocketsPrimaryCases() => [.. SocketsPrimaries];

    public static TheoryData<string, string, string> RedirectCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var primary in SocketsPrimaries)
        {
            foreach (var (auth, route) in new[] { ("credential", "GET message"), ("credential", "POST keys"), ("api key", "GET message"), ("api key", "POST keys") })
            {
                data.Add(primary, auth, route);
            }
        }

        return data;
    }

    /// <summary>
    /// A <c>Location</c> whose hostname spells the bearer value in hex, split into DNS labels. Following it would make
    /// the redirected hop's name-resolution failure name that host, so the pipeline does not follow, whichever way the
    /// caller configured its <see cref="SocketsHttpHandler"/>, redirects turned on included: the 3xx is final under
    /// spec §16 status precedence and surfaces as the mapped exception, once.
    /// </summary>
    [Theory]
    [MemberData(nameof(RedirectCases))]
    public async Task RedirectToAHostSpellingTheBearer_IsNotFollowed_AndSurfacesAsTheFinalStatus(string primary, string auth, string route)
    {
        await using var server = new EchoingServer(authorization =>
            $"HTTP/1.1 302 Found\r\nLocation: http://{HexHost(Bare(authorization))}/\r\nContent-Length: 0\r\n\r\n");
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        ConfigurePrimary(AddLoggedNachosClient(services, o => Configure(o, server.BaseAddress, auth)), primary);
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Call(provider.GetRequiredService<INachosClient>(), route));

        server.Requests.ShouldBe(1);
        ex.StatusCode.ShouldBe(HttpStatusCode.Found);
        ex.Message.ShouldContain("returned 302");
        ex.InnerException.ShouldBeNull();
        AssertNoSecretWindow(ex, logs.FormattedText, Secret(auth));
    }

    /// <summary>A 302 to another server that would answer 200: the call fails with the 302 and the other server is never asked.</summary>
    [Theory]
    [MemberData(nameof(SocketsPrimaryCases))]
    public async Task RedirectToAnotherServer_IsNotFollowed_SoTheCallFailsWithTheStatus(string primary)
    {
        await using var target = new EchoingServer(_ =>
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {MessageJson.Length}\r\n\r\n{MessageJson}");
        await using var server = new EchoingServer(_ =>
            $"HTTP/1.1 302 Found\r\nLocation: {target.BaseAddress}v3/workspaces/w1/sessions/s1/messages/m1\r\nContent-Length: 0\r\n\r\n");
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        ConfigurePrimary(AddLoggedNachosClient(services, o => Configure(o, server.BaseAddress, "credential")), primary);
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        ex.StatusCode.ShouldBe(HttpStatusCode.Found);
        server.Requests.ShouldBe(1);
        target.Requests.ShouldBe(0);
    }

    /// <summary>
    /// The wrapper stays below the <c>ClientHandler</c> logger whichever way the caller configured the primary
    /// handler: the raw <c>Received an invalid header name: 'Bearer …'</c> never reaches that log.
    /// </summary>
    [Theory]
    [MemberData(nameof(SocketsPrimaryCases))]
    public async Task EchoedBearer_AsAHeaderLine_IsReplacedBeforeTheClientHandlerLog_WithACallerConfiguredPrimary(string primary)
    {
        await using var server = new EchoingServer(authorization => $"HTTP/1.1 200 OK\r\n{authorization}: x\r\nContent-Length: 2\r\n\r\n{{}}");
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        ConfigurePrimary(AddLoggedNachosClient(services, o => Configure(o, server.BaseAddress, "credential")), primary);
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        server.Requests.ShouldBe(RetryHandler.MaxAttempts);
        ex.Message.ShouldBe(SecretRedaction.CannedMessage(HttpRequestError.InvalidResponse));
        var clientHandler = logs.FormattedTextOf(".ClientHandler");
        clientHandler.ShouldContain(SecretRedaction.CannedMessage(HttpRequestError.InvalidResponse));
        clientHandler.ShouldNotContain("Received an invalid header name");
        AssertNoSecretWindow(ex, logs.FormattedText, Token);
    }

    /// <summary>
    /// A primary handler of the caller's own that is not a <see cref="SocketsHttpHandler"/> keeps following redirects
    /// (documented on <c>AddNachosClient</c>): the redirected hop fails to resolve the hex hostname (through the
    /// connect callback, so no DNS query is made), and .NET's own text names that host. The kept connection failure is
    /// rebuilt without it, and the wrapper around that handler does so before the <c>ClientHandler</c> logger sees it,
    /// so neither the exception chain nor either factory log carries the value.
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
        AddLoggedNachosClient(services, o => Configure(o, server.BaseAddress, auth))
            .ConfigurePrimaryHttpMessageHandler(() => new CallersHandler
            {
                InnerHandler = new SocketsHttpHandler
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
                },
            });
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Call(provider.GetRequiredService<INachosClient>(), route));

        server.Requests.ShouldBe(route == "GET message" ? RetryHandler.MaxAttempts : 1);
        ex.HttpRequestError.ShouldBe(HttpRequestError.NameResolutionError);
        ex.Message.ShouldBe(SecretRedaction.ConnectionMessage(HttpRequestError.NameResolutionError, SocketError.HostNotFound));
        ex.InnerException.ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.HostNotFound);
        logs.FormattedTextOf(".LogicalHandler").ShouldContain("HTTP request failed");
        logs.FormattedTextOf(".ClientHandler").ShouldContain(SecretRedaction.ConnectionMessage(HttpRequestError.NameResolutionError, SocketError.HostNotFound));
        AssertNoSecretWindow(ex, logs.FormattedText, Secret(auth));
    }

    /// <summary>A key whose letters alternate in case: no 16-character run of it survives a change of case.</summary>
    private const string MixedCaseKey = "nK-mIxEd-CaSe-KeY-0a1B2c3D4e5F6g7H";

    public static TheoryData<string, string> EchoedNameCases() => new()
    {
        { "the bare value minus its first character", "credential" },
        { "the signature segment alone", "credential" },
        { "the lower-cased value", "credential" },
        { "the upper-cased value", "credential" },
        { "the lower-cased value", "mixed-case api key" },
        { "the upper-cased value", "mixed-case api key" },
        { "the key minus its first character", "api key" },
        { "the dash-separated hex of the value", "api key" },
        { "the dash-separated hex of the value", "credential" },
    };

    /// <summary>
    /// A response header whose name is only part of the bearer value, another case of it, or its hex, is removed
    /// before the <c>ClientHandler</c> logs the response headers at Trace, as the whole value is; the call itself
    /// succeeds.
    /// </summary>
    [Theory]
    [MemberData(nameof(EchoedNameCases))]
    public async Task PartialOrRecasedEchoOfTheBearer_AsAHeaderName_NeverReachesTheClientHandlerLog(string shape, string auth)
    {
        string? name = null;
        await using var server = new EchoingServer(authorization =>
        {
            var bare = Bare(authorization);
            name = shape switch
            {
                "the bare value minus its first character" => bare[1..],
                "the signature segment alone" => bare[(bare.LastIndexOf('.') + 1)..],
                "the lower-cased value" => bare.ToLowerInvariant(),
                "the upper-cased value" => bare.ToUpperInvariant(),
                "the dash-separated hex of the value" => BitConverter.ToString(Encoding.ASCII.GetBytes(bare)),
                _ => bare[1..],
            };
            return $"HTTP/1.1 200 OK\r\n{name}: x\r\nContent-Type: application/json\r\nContent-Length: {MessageJson.Length}\r\n\r\n{MessageJson}";
        });
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        AddLoggedNachosClient(services, o =>
        {
            Configure(o, server.BaseAddress, auth == "mixed-case api key" ? "api key" : auth);
            if (auth == "mixed-case api key")
            {
                o.ApiKey = MixedCaseKey;
            }
        });
        await using var provider = services.BuildServiceProvider();

        var message = await provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1");

        var secret = auth == "mixed-case api key" ? MixedCaseKey : Secret(auth);
        message.Id.ShouldBe("m1");
        server.Requests.ShouldBe(1);
        server.Authorizations.ShouldAllBe(a => a == "Bearer " + secret);
        var clientHandler = logs.FormattedTextOf(".ClientHandler");
        clientHandler.ShouldContain("Content-Type", Case.Insensitive);
        clientHandler.Contains(name!, StringComparison.OrdinalIgnoreCase).ShouldBeFalse($"the name ({shape}) reached the ClientHandler log");
        SecretScan.FindLeak(logs.FormattedText, secret, window: 12).ShouldBeNull("the logs leak the secret");
        SecretScan.FindLeak(logs.FormattedText.ToLowerInvariant(), secret.ToLowerInvariant(), window: 12).ShouldBeNull("the logs leak the secret in another case");
    }

    /// <summary>
    /// A name that shares fewer than 16 characters with the bearer value is an ordinary header and is kept, as is the
    /// <c>Retry-After</c> beside it, which still decides the retry.
    /// </summary>
    [Fact]
    public async Task NameSharingOnly15Characters_IsKept_AndRetryAfterStillSurfaces()
    {
        string? name = null;
        await using var server = new EchoingServer(authorization =>
        {
            name = "Echo~" + Bare(authorization)[100..115] + "~X";
            return $"HTTP/1.1 503 Service Unavailable\r\n{name}: x\r\nRetry-After: 40\r\nContent-Type: application/json\r\nContent-Length: 17\r\n\r\n{{\"detail\":\"busy\"}}";
        });
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        AddLoggedNachosClient(services, o => Configure(o, server.BaseAddress, "credential"));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        server.Requests.ShouldBe(1);
        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromSeconds(40));
        var clientHandler = logs.FormattedTextOf(".ClientHandler");
        clientHandler.ShouldContain(name!);
        clientHandler.ShouldContain("Retry-After");
    }

    /// <summary>
    /// Names are matched against a bearer value of at least 16 characters: a 16-character key echoed as a name is
    /// removed, a 15-character one is not (the shorter the value, the likelier an ordinary name holds it).
    /// </summary>
    [Theory]
    [InlineData("k-0123456789abcd", true)]
    [InlineData("k-0123456789abc", false)]
    public async Task ApiKeyEchoedAsAHeaderName_IsRemovedFrom16Characters(string key, bool removed)
    {
        await using var server = new EchoingServer(authorization =>
            $"HTTP/1.1 200 OK\r\n{Bare(authorization)}: x\r\nContent-Type: application/json\r\nContent-Length: {MessageJson.Length}\r\n\r\n{MessageJson}");
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        AddLoggedNachosClient(services, o =>
        {
            o.BaseAddress = server.BaseAddress;
            o.ApiKey = key;
        });
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1");

        var clientHandler = logs.FormattedTextOf(".ClientHandler");
        clientHandler.ShouldContain("Content-Type", Case.Insensitive);
        clientHandler.Contains(key + ":", StringComparison.Ordinal).ShouldBe(!removed);
    }

    /// <summary>
    /// <c>AddNachosClient</c> with the factory's default loggers added back, as a caller may do: these tests observe
    /// what the wrapper keeps out of those loggers (exception text, header names). Their structured state carries every
    /// header value, the request's own <c>Authorization</c> included, so the assertions read the formatted text only;
    /// the default pipeline, which has no such loggers, is covered by
    /// <see cref="HeaderValues_NeverReachTheLogs_StructuredStateIncluded"/>.
    /// </summary>
    private static IHttpClientBuilder AddLoggedNachosClient(IServiceCollection services, Action<NachosClientOptions> configure) =>
        services.AddNachosClient(configure).AddDefaultLogger();

    private static void ConfigurePrimary(IHttpClientBuilder http, string primary)
    {
        switch (primary)
        {
            case "default":
                break;
            case "UseSocketsHttpHandler turning redirects on":
                http.UseSocketsHttpHandler((handler, _) =>
                {
                    handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
                    handler.AllowAutoRedirect = true;
                });
                break;
            case "ConfigurePrimaryHttpMessageHandler with a SocketsHttpHandler following redirects":
                http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = true });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(primary), primary, "unknown primary handler configuration");
        }
    }

    /// <summary>A primary handler of the caller's own that is not a <see cref="SocketsHttpHandler"/>.</summary>
    private sealed class CallersHandler : DelegatingHandler
    {
    }

    /// <summary>
    /// A short API key is a substring of ordinary header names (<c>e</c>, <c>ry</c> and <c>After</c> are inside
    /// <c>Retry-After</c>). Matching it against names would strip the server's <c>Retry-After</c> from a 503, so the
    /// over-cap delay would not surface and the call would be retried three times instead of once. Names are matched
    /// only against values of at least <see cref="RedactionSecrets.MinHeaderNameMatchLength"/> characters.
    /// </summary>
    [Theory]
    [InlineData("e")]
    [InlineData("ry")]
    [InlineData("After")]
    public async Task ShortApiKey_DoesNotStripResponseHeaders_SoRetryAfterStillSurfaces(string key)
    {
        await using var server = new EchoingServer(_ =>
            "HTTP/1.1 503 Service Unavailable\r\nRetry-After: 40\r\nContent-Type: application/json\r\nContent-Length: 17\r\n\r\n{\"detail\":\"busy\"}");
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        AddLoggedNachosClient(services, o =>
        {
            o.BaseAddress = server.BaseAddress;
            o.ApiKey = key;
        });
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        server.Requests.ShouldBe(1);
        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromSeconds(40));
        ex.Message.ShouldEndWith(" Retry-After: 40s.");
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

    internal static string JwtShaped(int length)
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
