using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Core;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// <c>services.AddNachosClient(...)</c>: options validated with value-free messages, one named HttpClient whose
/// pipeline is <see cref="RetryHandler"/> over the primary handler, one <see cref="TimeProvider"/> from the container
/// shared by the handler and the client, and exactly one <see cref="INachosClient"/> even next to <c>AddNachos</c>.
/// </summary>
public sealed class AddNachosClientTests
{
    private const string ApiKey = "nk-CANARY-DI-APIKEY-93b1";

    private const string Token = "eyJ.CANARY-DI-TOKEN-5e7d.sig";

    private static readonly Uri Base = new("https://nachos.test/root/");

    private static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","metadata":{},"created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    [Fact]
    public async Task Registers_INachosClient_AsNachosHttpClient_OverTheRetryPipeline()
    {
        var stub = new StubHandler((_, attempt) => attempt < 3
            ? StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}""")
            : StubHandler.Json(HttpStatusCode.OK, MessageJson));
        using var provider = Provider(stub, o => o.ApiKey = ApiKey);

        var client = provider.GetRequiredService<INachosClient>();
        var message = await client.GetMessageAsync("w1", "s1", "m1");

        client.ShouldBeOfType<NachosHttpClient>();
        message.Id.ShouldBe("m1");
        stub.Requests.Count.ShouldBe(RetryHandler.MaxAttempts);
        stub.Requests.ShouldAllBe(r => r.Authorization == "Bearer " + ApiKey);
        stub.Requests[0].Uri.ShouldBe(new Uri("https://nachos.test/root/v3/workspaces/w1/sessions/s1/messages/m1"));
    }

    [Fact]
    public void NachosHttpClient_IsResolvableByItsOwnType()
    {
        using var provider = Provider(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson)));

        provider.GetRequiredService<NachosHttpClient>().ShouldNotBeNull();
    }

    [Fact]
    public void NamedHttpClient_HasTheBaseAddress_AndTheDefaultOverallTimeout()
    {
        using var provider = Provider(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson)));

        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(NachosClientServiceCollectionExtensions.HttpClientName);

        http.BaseAddress.ShouldBe(Base);
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(100));
    }

    [Fact]
    public async Task Credential_IsUsed_ThroughDI()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        using var provider = Provider(stub, o =>
        {
            o.ApiKey = ApiKey;
            o.Credential = new StaticCredential(Token);
            o.Scopes = ["api://nachos/.default"];
        });

        await provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1");

        stub.Requests.Single().Authorization.ShouldBe("Bearer " + Token);
    }

    public static TheoryData<string, Action<NachosClientOptions>> InvalidOptions => new()
    {
        { "missing base address", o => o.BaseAddress = null! },
        { "relative base address", o => o.BaseAddress = new Uri("/v3", UriKind.Relative) },
        { "ftp base address", o => o.BaseAddress = new Uri("ftp://nachos.test/") },
        { "api key with whitespace", o => o.ApiKey = ApiKey + " x" },
        { "api key with CRLF", o => o.ApiKey = ApiKey + "\r\nX-Injected: 1" },
        { "credential without scopes", o => o.Credential = new StaticCredential(Token) },
        { "credential with a blank scope", o => { o.Credential = new StaticCredential(Token); o.Scopes = [" "]; } },
        { "zero attempt timeout", o => o.AttemptTimeout = TimeSpan.Zero },
        { "negative attempt timeout", o => o.AttemptTimeout = TimeSpan.FromSeconds(-1) },
        { "attempt timeout beyond the timer range", o => o.AttemptTimeout = TimeSpan.FromDays(30) },
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidOptions_FailOnResolution_WithValueFreeMessages(string _, Action<NachosClientOptions> breakIt)
    {
        using var provider = Provider(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson)), o =>
        {
            o.ApiKey = ApiKey;
            breakIt(o);
        });

        var ex = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<INachosClient>());

        ex.ToString().ShouldNotContain(ApiKey);
        ex.ToString().ShouldNotContain(Token);
        ex.ToString().ShouldNotContain("nachos.test");
        ex.Failures.ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidOptions_FailTheStartupValidation(string _, Action<NachosClientOptions> breakIt)
    {
        using var provider = Provider(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson)), breakIt);

        // What a host runs at start (ValidateOnStart), before any request is made.
        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void ValidOptions_PassTheStartupValidation()
    {
        using var provider = Provider(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson)), o =>
        {
            o.ApiKey = ApiKey;
            o.AttemptTimeout = Timeout.InfiniteTimeSpan;
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void DefaultAttemptTimeout_Is30Seconds()
    {
        new NachosClientOptions { BaseAddress = Base }.AttemptTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AttemptTimeout_IsPlumbedToTheRetryHandler_OnTheContainersClock()
    {
        var time = new FakeTimeProvider(Epoch);
        var stalled = new SemaphoreSlim(0);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var primary = new LambdaHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) > 1)
            {
                return StubHandler.Json(HttpStatusCode.OK, MessageJson);
            }

            stalled.Release();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            throw new IOException("the attempt timeout never fired");
        });
        using var provider = Provider(primary, o => o.AttemptTimeout = TimeSpan.FromSeconds(5), time);

        var call = provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1");
        (await stalled.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(5));

        // Exactly the configured 5 s of the container's clock ends the attempt; the 30 s default would not.
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Backoff after the timeout also waits on the fake clock (at most 0.5 s with full jitter).
        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        (await call).Id.ShouldBe("m1");
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task OneTimeProvider_ServesTheRetryHandlerAndTheClient()
    {
        // The container's clock is in 2000. A Retry-After date 60 s past it is over the 30 s cap only on that clock:
        // the handler must hand the 429 back at once (on the system clock the date is long past, so it would retry),
        // and the client must surface 60 s (on the system clock it would be 0).
        var time = new FakeTimeProvider(Epoch);
        var stub = new StubHandler((_, _) =>
        {
            var response = StubHandler.Json(HttpStatusCode.TooManyRequests, """{"detail":"slow down"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(Epoch.AddSeconds(60));
            return response;
        });
        using var provider = Provider(stub, configure: null, time);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        stub.Requests.Count.ShouldBe(1);
        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task BackoffWaits_RunOnTheContainersClock()
    {
        var time = new FakeTimeProvider(Epoch);
        var stub = new StubHandler((_, attempt) =>
        {
            if (attempt > 1)
            {
                return StubHandler.Json(HttpStatusCode.OK, MessageJson);
            }

            var response = StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        using var provider = Provider(stub, configure: null, time);

        var call = provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1");
        await WaitUntilAsync(() => stub.Requests.Count == 1);
        await Task.Delay(50);
        call.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(30));

        (await call.WaitAsync(TimeSpan.FromSeconds(10))).Id.ShouldBe("m1");
        stub.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public void ContainerTimeProvider_RegisteredAfterwards_StillWins()
    {
        var services = new ServiceCollection();
        services.AddNachosClient(o => o.BaseAddress = Base);
        var time = new FakeTimeProvider(Epoch);
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(time);
    }

    [Fact]
    public async Task DefaultResponseBufferCap_IsWiredThroughDI_AndIsNotRetried()
    {
        var stub = new StubHandler((_, _) =>
        {
            var content = new StreamContent(new ZeroStream(RetryHandler.DefaultMaxResponseBufferSize + 1));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var provider = Provider(stub);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        ex.HttpRequestError.ShouldBe(HttpRequestError.ConfigurationLimitExceeded);
        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CalledTwice_KeepsOnePipeline_AndAppliesBothConfigurationsInOrder()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}"""));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddNachosClient(o =>
        {
            o.BaseAddress = new Uri("https://first.test/");
            o.ApiKey = ApiKey;
        });
        services.AddNachosClient(o => o.BaseAddress = Base).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();

        await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1"));

        stub.Requests.Count.ShouldBe(RetryHandler.MaxAttempts);
        stub.Requests[0].Uri.Host.ShouldBe("nachos.test");
        stub.Requests[0].Authorization.ShouldBe("Bearer " + ApiKey);
        services.Count(d => d.ServiceType == typeof(INachosClient)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(NachosHttpClient)).ShouldBe(1);
    }

    [Fact]
    public void AfterAddNachos_TheHttpClientReplacesTheInProcessClient()
    {
        var services = new ServiceCollection();
        services.AddNachos(nachos => nachos.UseInMemory());
        services.AddNachosClient(o => o.BaseAddress = Base);

        AssertSingleHttpClient(services);
    }

    [Fact]
    public void BeforeAddNachos_TheHttpClientStays_AndNoSecondClientIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddNachosClient(o => o.BaseAddress = Base);
        services.AddNachos(nachos => nachos.UseInMemory());

        AssertSingleHttpClient(services);
    }

    [Fact]
    public async Task AuthorizationValues_NeverReachTheLogs()
    {
        var logs = new CapturingLoggerProvider();
        foreach (var credential in new TokenCredential?[] { null, new StaticCredential(Token) })
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddNachosClient(o =>
            {
                o.BaseAddress = Base;
                o.ApiKey = ApiKey;
                o.Credential = credential;
                o.Scopes = ["api://nachos/.default"];
            }).ConfigurePrimaryHttpMessageHandler(() => new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson)));
            using var provider = services.BuildServiceProvider();

            await provider.GetRequiredService<INachosClient>().GetMessageAsync("w1", "s1", "m1");
        }

        var text = logs.Text;
        text.ShouldContain("Authorization");
        text.ShouldNotContain(ApiKey);
        text.ShouldNotContain(Token);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => ((IServiceCollection)null!).AddNachosClient(o => o.BaseAddress = Base));
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddNachosClient(null!));
    }

    private static void AssertSingleHttpClient(ServiceCollection services)
    {
        services.Count(d => d.ServiceType == typeof(INachosClient)).ShouldBe(1);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<INachosClient>().Single().ShouldBeOfType<NachosHttpClient>();

        // The in-process service is still there under its own type.
        scope.ServiceProvider.GetRequiredService<NachosService>().ShouldNotBeNull();
    }

    private static ServiceProvider Provider(
        HttpMessageHandler primary, Action<NachosClientOptions>? configure = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(time ?? new ZeroDelayTimeProvider());
        services.AddNachosClient(o =>
        {
            o.BaseAddress = Base;
            configure?.Invoke(o);
        }).ConfigurePrimaryHttpMessageHandler(() => primary);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not reached");
            await Task.Delay(5);
        }
    }

    private sealed class StaticCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    /// <summary>A body of <paramref name="length"/> zero bytes of unknown length, produced lazily.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, _remaining);
            buffer[..n].Clear();
            _remaining -= n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
