using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Testing;

namespace Nachos.Client.Tests;

/// <summary>
/// One round-trip pair: <see cref="Http"/> is <see cref="NachosHttpClient"/> built by <c>AddNachosClient</c> (so the
/// real <see cref="RetryHandler"/> pipeline) against a fresh <see cref="NachosApiFactory"/> server, and
/// <see cref="InProcess"/> is the in-process client of <c>AddNachos(b =&gt; b.UseInMemory())</c> in a separate
/// container. Both stores start empty, and both clocks are frozen at <see cref="Start"/>, so server-generated
/// <c>created_at</c> values are deterministic and compared as they are.
/// </summary>
/// <remarks>
/// Seams for later tasks, so their round trips need no restructuring: <c>configureServer</c> reaches the server's
/// host builder (Task 10: enable authentication, signing keys, grants) and <c>configureClient</c> the client options
/// (Task 10: <see cref="NachosClientOptions.ApiKey"/> or <see cref="NachosClientOptions.Credential"/>). Task 11 replay
/// round trips call <see cref="INachosClient.CreateMessagesAsync"/> twice with one <c>idempotencyKey</c> on both
/// clients and read <see cref="Wire"/> for the attempts that reached the server.
/// </remarks>
internal sealed class RoundTripHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly NachosApiFactory _factory;
    private readonly WebApplicationFactory<Program> _server;
    private readonly ServiceProvider _httpServices;
    private readonly ServiceProvider _inProcessServices;
    private readonly IServiceScope _inProcessScope;

    public RoundTripHarness(Action<NachosClientOptions>? configureClient = null, Action<IWebHostBuilder>? configureServer = null)
    {
        _factory = new NachosApiFactory();
        _server = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(Start)));
            configureServer?.Invoke(builder);
        });
        var testServer = _server.Server;

        var http = new ServiceCollection();
        http.AddNachosClient(options =>
            {
                options.BaseAddress = testServer.BaseAddress;
                configureClient?.Invoke(options);
            })
            .ConfigurePrimaryHttpMessageHandler(testServer.CreateHandler)
            .AddHttpMessageHandler(() => new WireRecorder(Wire));
        _httpServices = http.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Http = _httpServices.GetRequiredService<INachosClient>();

        var inProcess = new ServiceCollection();
        inProcess.AddSingleton<TimeProvider>(new FakeTimeProvider(Start));
        inProcess.AddLogging();
        inProcess.AddNachos(nachos => nachos.UseInMemory());
        _inProcessServices = inProcess.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _inProcessScope = _inProcessServices.CreateScope();
        InProcess = _inProcessScope.ServiceProvider.GetRequiredService<INachosClient>();
    }

    /// <summary>The HTTP client under test, over the retry pipeline.</summary>
    public INachosClient Http { get; }

    /// <summary>The in-process reference client.</summary>
    public INachosClient InProcess { get; }

    /// <summary>Every attempt that left <see cref="RetryHandler"/> for the server, as <c>"METHOD /path?query"</c>.</summary>
    public WireLog Wire { get; } = new();

    /// <summary>
    /// The HTTP client's own named <see cref="HttpClient"/> (retry pipeline, wire log, test server), for requests
    /// <see cref="INachosClient"/> cannot express (Task 11: a replay without the key; staged-gap request bodies).
    /// </summary>
    public HttpClient CreatePipelineClient() =>
        _httpServices.GetRequiredService<IHttpClientFactory>().CreateClient(NachosClientServiceCollectionExtensions.HttpClientName);

    public void Dispose()
    {
        _inProcessScope.Dispose();
        _inProcessServices.Dispose();
        _httpServices.Dispose();
        _server.Dispose();
        _factory.Dispose();
    }

    internal sealed class WireLog
    {
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public void Add(string request)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }
        }
    }

    /// <summary>Sits below <see cref="RetryHandler"/>, so it sees each attempt.</summary>
    private sealed class WireRecorder(WireLog log) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            log.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
