using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
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
        { "null scopes without a credential", o => o.Scopes = null! },
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

    public static TheoryData<string> PrimaryConfigurations() =>
    [
        "default",
        "UseSocketsHttpHandler turning redirects on",
        "ConfigurePrimaryHttpMessageHandler with a SocketsHttpHandler following redirects",
        "ConfigurePrimaryHttpMessageHandler tuning the registered handler",
        "ConfigurePrimaryHttpMessageHandler with a wrapper already in place",
        "AddNachosClient called twice",
    ];

    /// <summary>
    /// Whatever the caller does to the primary handler on the returned builder, the built chain ends in exactly one
    /// <see cref="TransportRedactionHandler"/> directly over a <see cref="SocketsHttpHandler"/> that does not follow
    /// redirects; a delegate tuning the handler receives that <see cref="SocketsHttpHandler"/>, and its settings survive.
    /// </summary>
    [Theory]
    [MemberData(nameof(PrimaryConfigurations))]
    public void PrimaryHandler_EndsUpWrappedOnce_OverASocketsHttpHandlerThatNeverFollowsRedirects(string configuration)
    {
        var services = new ServiceCollection();
        var http = services.AddNachosClient(o => o.BaseAddress = Base);
        switch (configuration)
        {
            case "UseSocketsHttpHandler turning redirects on":
                http.UseSocketsHttpHandler((handler, _) =>
                {
                    handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
                    handler.AllowAutoRedirect = true;
                });
                break;
            case "ConfigurePrimaryHttpMessageHandler with a SocketsHttpHandler following redirects":
                http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = true, PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
                break;
            case "ConfigurePrimaryHttpMessageHandler tuning the registered handler":
                http.ConfigurePrimaryHttpMessageHandler((handler, _) => ((SocketsHttpHandler)handler).PooledConnectionLifetime = TimeSpan.FromMinutes(2));
                break;
            case "ConfigurePrimaryHttpMessageHandler with a wrapper already in place":
                // Only this assembly's tests can do this (the type is internal): the filter must not wrap it again, and
                // it looks no further than the primary handler, so the redirect setting is the test's own here.
                http.ConfigurePrimaryHttpMessageHandler(() => new TransportRedactionHandler
                {
                    InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) },
                });
                break;
            case "AddNachosClient called twice":
                services.AddNachosClient(o => o.ApiKey = ApiKey);
                break;
        }

        using var provider = services.BuildServiceProvider();
        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(NachosClientServiceCollectionExtensions.HttpClientName)).ToList();

        services.Count(d => d.ImplementationType == typeof(TransportRedactionFilter)).ShouldBe(1);
        chain.OfType<RetryHandler>().Count().ShouldBe(1);
        var wrapper = chain.OfType<TransportRedactionHandler>().ShouldHaveSingleItem();
        var sockets = chain[^1].ShouldBeOfType<SocketsHttpHandler>();
        wrapper.InnerHandler.ShouldBeSameAs(sockets);
        sockets.AllowAutoRedirect.ShouldBeFalse();
        if (configuration != "default" && configuration != "AddNachosClient called twice")
        {
            sockets.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
        }
    }

    [Fact]
    public void APrimaryHandlerOfAnotherType_IsWrappedToo_AndLeftAsItIs()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var services = new ServiceCollection();
        services.AddNachosClient(o => o.BaseAddress = Base).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(NachosClientServiceCollectionExtensions.HttpClientName)).ToList();

        chain.OfType<TransportRedactionHandler>().ShouldHaveSingleItem().InnerHandler.ShouldBeSameAs(stub);
        chain[^1].ShouldBeSameAs(stub);
    }

    [Fact]
    public void OtherNamedClients_AreNotWrapped()
    {
        var services = new ServiceCollection();
        services.AddNachosClient(o => o.BaseAddress = Base);
        services.AddHttpClient("other");
        using var provider = services.BuildServiceProvider();

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("other")).ToList();

        chain.OfType<TransportRedactionHandler>().ShouldBeEmpty();
        chain.OfType<RetryHandler>().ShouldBeEmpty();

        // The factory's own default primary handler, with its own default for redirects.
        chain[^1].ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeTrue();
    }

    private const string ResilienceHandlerTypeName = "Microsoft.Extensions.Http.Resilience.ResilienceHandler";

    public static TheoryData<string> ResilienceDefaultsOrder() => ["defaults before AddNachosClient", "defaults after AddNachosClient"];

    /// <summary>
    /// <c>AddStandardResilienceHandler</c> in <c>ConfigureHttpClientDefaults</c> (the service-defaults shape) puts a
    /// <c>ResilienceHandler</c> above <see cref="RetryHandler"/> on every named client; it retries by status alone,
    /// breaking the spec §16 rules: a key creation answered 503 would go out four times, a 501 four times, a keyed
    /// create twelve. The client removes it from its own chain, whichever order the two were configured in, and leaves
    /// other clients alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(ResilienceDefaultsOrder))]
    public async Task StandardResilienceHandler_IsRemovedFromThisClient_SoTheSpecRetryRulesHold(string order)
    {
        var stub = new StubHandler((request, _) => request.Uri.AbsolutePath switch
        {
            var path when path.EndsWith("/v3/keys", StringComparison.Ordinal) => StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}"""),
            var path when path.EndsWith("/messages/m1", StringComparison.Ordinal) =>
                StubHandler.Json(HttpStatusCode.NotImplemented, """{"detail":"Not implemented in this Nachos version"}"""),
            _ => StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}"""),
        });
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ZeroDelayTimeProvider());
        services.AddHttpClient("other");
        if (order == "defaults before AddNachosClient")
        {
            services.ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler());
        }

        services.AddNachosClient(o => o.BaseAddress = Base).ConfigurePrimaryHttpMessageHandler(() => stub);
        if (order == "defaults after AddNachosClient")
        {
            services.ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler());
        }

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<INachosClient>();
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        (await Should.ThrowAsync<HttpRequestException>(() => client.CreateKeyAsync("w1"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Should.ThrowAsync<HttpRequestException>(() => client.GetMessageAsync("w1", "s1", "m1"))).StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await Should.ThrowAsync<HttpRequestException>(() => client.CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")], "key-1")))
            .StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        stub.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/v3/keys", StringComparison.Ordinal)).ShouldBe(1, "a key creation is never retried");
        stub.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/messages/m1", StringComparison.Ordinal)).ShouldBe(1, "a 501 is final");
        stub.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)).ShouldBe(RetryHandler.MaxAttempts, "a keyed create is retried by RetryHandler alone");
        Chain(factory.CreateHandler(NachosClientServiceCollectionExtensions.HttpClientName)).Count(h => h.GetType().FullName == ResilienceHandlerTypeName).ShouldBe(0);
        Chain(factory.CreateHandler("other")).Count(h => h.GetType().FullName == ResilienceHandlerTypeName).ShouldBe(1, "other clients keep their resilience handler");
    }

    /// <summary>
    /// With the resilience defaults configured, a <c>Retry-After</c> over the cap still surfaces at once with its delay
    /// (the standard handler would wait it out and resend), and the attempt timeout is still the client's own, on the
    /// container's clock (the standard handler's per-attempt timeout runs on the system clock).
    /// </summary>
    [Fact]
    public async Task RetryAfterAndTheAttemptTimeout_StayTheClientsOwn_WithTheResilienceDefaults()
    {
        var time = new FakeTimeProvider(Epoch);
        var stalled = new SemaphoreSlim(0);
        var attempts = 0;
        var primary = new LambdaHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v3/keys", StringComparison.Ordinal))
            {
                var busy = StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}""");
                busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(40));
                return busy;
            }

            if (Interlocked.Increment(ref attempts) > 1)
            {
                return StubHandler.Json(HttpStatusCode.OK, MessageJson);
            }

            stalled.Release();
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            throw new IOException("the attempt timeout never fired");
        });
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler());
        services.AddNachosClient(o =>
        {
            o.BaseAddress = Base;
            o.AttemptTimeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(() => primary);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<INachosClient>();

        var busy = await Should.ThrowAsync<HttpRequestException>(() => client.CreateKeyAsync("w1"));
        NachosExceptionData.TryGetRetryAfter(busy, out var delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromSeconds(40));

        var call = client.GetMessageAsync("w1", "s1", "m1");
        (await stalled.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(5));
        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        (await call).Id.ShouldBe("m1");
        attempts.ShouldBe(2);
    }

    /// <summary>The handlers from <paramref name="top"/> down to the primary handler.</summary>
    private static IEnumerable<HttpMessageHandler> Chain(HttpMessageHandler top)
    {
        for (var handler = top; handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler)
        {
            yield return handler;
        }
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

    /// <summary>
    /// The factory's built-in loggers put every header value, the request's own <c>Authorization</c> included, into
    /// the structured state of their Trace entries (only the formatted text is redacted), so <c>AddNachosClient</c>
    /// removes them: at Trace, nothing is logged under the factory's categories for this client, and the bearer value
    /// is nowhere in the capture. A caller who adds them back (<c>AddDefaultLogger</c>) takes that on, as documented.
    /// </summary>
    [Fact]
    public async Task TheFactorysLoggers_AreRemoved_SoNoHeaderValueIsLogged()
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

        logs.Categories.Where(c => c.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)).ShouldBeEmpty("the factory's loggers must be removed");
        logs.Text.ShouldNotContain(ApiKey);
        logs.Text.ShouldNotContain(Token);
    }

    /// <summary>
    /// The capture itself must see what a structured provider sees: the factory's header log state holds each header's
    /// values as a <c>string[]</c>, which a naive capture prints as its type name and misses.
    /// </summary>
    [Fact]
    public void TheCapture_ExpandsStructuredCollections()
    {
        var logs = new CapturingLoggerProvider();
        var logger = logs.CreateLogger("Probe");
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Authorization", new[] { "Bearer " + Token }),
            new("Nested", new object[] { new KeyValuePair<string, object?>("k", new List<string> { "v1", "v2" }) }),
        };

        logger.Log(LogLevel.Trace, new EventId(1), state, null, (_, _) => "formatted only");

        logs.Text.ShouldContain("Authorization=[Bearer " + Token + "]");
        logs.Text.ShouldContain("Nested=[k=[v1, v2]]");
        logs.Text.ShouldNotContain("System.String[]");
        logs.FormattedText.ShouldContain("formatted only");
        logs.FormattedText.ShouldNotContain(Token);
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
