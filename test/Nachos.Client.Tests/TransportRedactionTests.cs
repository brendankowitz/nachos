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
    private const string Token = "eyJ.CANARY-TRANSPORT-TOKEN-91c4.sig";

    private const string ApiKey = "nk-CANARY-TRANSPORT-APIKEY-6a0e";

    public static TheoryData<string, string, string> RealSocketCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var failure in new[] { "invalid header name", "invalid header line", "invalid trailer after a chunked 200", "invalid trailer after a 503 with Retry-After" })
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
        Chain(ex).ShouldContain(e => e.Message.Contains(ErrorMapper.Redacted, StringComparison.Ordinal), "the echo must have been redacted, not absent");
        logs.Text.ShouldNotContain(Secret(auth));
        if (failure.Contains("503", StringComparison.Ordinal) && route == "GET message")
        {
            ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
            delay.ShouldBe(TimeSpan.FromSeconds(1));
            ex.Message.ShouldEndWith(" Retry-After: 1s.");
        }
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
        ex.Message.ShouldContain("Received an invalid header name: 'Bearer " + ErrorMapper.Redacted + "'.");
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
    public async Task TransportFailure_WithoutTheBearer_KeepsItsOriginalException()
    {
        var original = new HttpRequestException("connection refused (nachos.test:443)", new SocketException(10061));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o => Configure(o, new Uri("https://nachos.test/"), "api key"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((_, _) => throw original));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().CreateKeyAsync("w"));

        ex.ShouldBeSameAs(original);
        ex.InnerException.ShouldBeOfType<SocketException>();
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
        foreach (var secret in new[] { Secret(auth), Token, ApiKey })
        {
            ex.ToString().ShouldNotContain(secret);
            foreach (var current in Chain(ex))
            {
                current.Message.ShouldNotContain(secret);
                current.ToString().ShouldNotContain(secret);
                foreach (DictionaryEntry entry in current.Data)
                {
                    $"{entry.Key}={entry.Value}".ShouldNotContain(secret);
                }
            }
        }
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

                await stream.WriteAsync(Encoding.ASCII.GetBytes(_respond(authorization)), _stop.Token);
                await stream.FlushAsync(_stop.Token);
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
