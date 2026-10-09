using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Client;

// Same namespace as AddNachos and AddHttpClient, so callers need no extra using.
namespace Microsoft.Extensions.DependencyInjection;

public static class NachosClientServiceCollectionExtensions
{
    /// <summary>The name of the <see cref="HttpClient"/> that <see cref="AddNachosClient"/> registers.</summary>
    internal const string HttpClientName = "Nachos.Client";

    /// <summary>
    /// Registers <see cref="NachosHttpClient"/> as the <see cref="INachosClient"/>, over a named
    /// <see cref="HttpClient"/> whose pipeline is <see cref="RetryHandler"/> above a <see cref="SocketsHttpHandler"/>.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Sets the <see cref="NachosClientOptions"/>.</param>
    /// <returns>
    /// The builder of the named client, for adding handlers (they go below <see cref="RetryHandler"/>, so they see
    /// every attempt) or replacing the primary handler.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Options.</b> The options are validated with the rules documented on <see cref="NachosClientOptions"/> at host
    /// start (<c>ValidateOnStart</c>) and whenever they are first read, so resolving the client with invalid options
    /// throws <see cref="OptionsValidationException"/>. Messages never contain a configured value. The options are read
    /// once per container.
    /// </para>
    /// <para>
    /// <b>Pipeline.</b> The client's <see cref="HttpClient.BaseAddress"/> is <see cref="NachosClientOptions.BaseAddress"/>.
    /// The primary <see cref="SocketsHttpHandler"/> does not follow redirects (<c>AllowAutoRedirect</c> is false): a 3xx
    /// is a final status under spec §16 status precedence and surfaces as the mapped <see cref="HttpRequestException"/>
    /// with that status, sent once. Following it would let the server pick the next hop's host, which .NET names in a
    /// connection failure's text and in <c>System.Net.NameResolution</c> events; and .NET drops <c>Authorization</c> on
    /// the redirected hop, so an authenticated API gains nothing from redirects.
    /// <see cref="RetryHandler"/> gets <see cref="NachosClientOptions.AttemptTimeout"/>. <see cref="HttpClient.Timeout"/>
    /// keeps its default of 100 s and stays the overall bound of a call, retries and waits included; change it with
    /// <c>ConfigureHttpClient</c> on the returned builder. With the default 30 s attempt timeout, a call whose retries
    /// honour <c>Retry-After</c> can need up to 150 s (three attempts and two 30 s waits); at 100 s it is cut off and
    /// surfaces as a <see cref="TaskCanceledException"/> without the status or the requested delay, so raise the timeout
    /// to at least 150 s if every honoured wait must complete.
    /// </para>
    /// <para>
    /// <b>Secrets in logs and exceptions.</b> A server that reflects the request's credentials back into its response is
    /// misconfigured or hostile; the client still keeps the bearer value (token or API key) out of what it controls:
    /// <list type="bullet">
    /// <item><description>
    /// exception text: transport failures whose text can repeat server bytes (malformed status lines, headers, chunks or
    /// trailers, and anything not known to be safe) are replaced by fixed text naming only their
    /// <see cref="HttpRequestError"/>, keeping the status and the <c>Retry-After</c> data; known-safe connection failures,
    /// timeouts and cancellations are kept, with the bearer value redacted from them, and a connection failure's text is
    /// rebuilt without the target host and port (a primary handler of your own that follows redirects lets the server
    /// choose that host). <see cref="RetryHandler"/> and <see cref="NachosHttpClient"/> apply this whatever the primary
    /// handler (see <c>SecretRedaction</c>);
    /// </description></item>
    /// <item><description>
    /// the <see cref="IHttpClientFactory"/> <c>ClientHandler</c> and <c>LogicalHandler</c> logs: the primary
    /// <see cref="SocketsHttpHandler"/> is wrapped in a handler that applies the same rule before those loggers see a
    /// failure, and removes a response header whose name holds the bearer value (a bare JWT is a valid header name, which
    /// the factory would log). Header values are redacted by the factory's default; do not turn that off for this client.
    /// <c>ConfigurePrimaryHttpMessageHandler</c> on the returned builder replaces the wrapper, so with your own primary
    /// handler the <c>ClientHandler</c> log of such a failure is no longer covered (exception text still is);
    /// </description></item>
    /// <item><description>
    /// server text the client shows on purpose (error bodies and reason phrases mapped to exceptions): the bearer value
    /// is redacted as plain text and as hex.
    /// </description></item>
    /// </list>
    /// Not covered:
    /// <list type="bullet">
    /// <item><description>
    /// <c>System.Net.Http</c> diagnostics raised by the framework itself, before any of this code runs: the
    /// <c>System.Net.Http</c> EventSource (for example <c>RequestFailedDetailed</c>, which carries the raw exception text,
    /// including a body that <see cref="HttpClient"/> buffers for a never-retried request), <c>DiagnosticSource</c>
    /// events and activity exception events recorded by tracing. Do not enable them at verbose levels against a server
    /// you do not trust;
    /// </description></item>
    /// <item><description>what your own handlers log;</description></item>
    /// <item><description>
    /// in mapped server text, echoes in another encoding (percent-encoding, base64), another letter case, or only part of
    /// the value; and non-string <see cref="Exception.Data"/> values (a <c>string[]</c>, a <see cref="Uri"/>).
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Handlers added to every client by <c>ConfigureHttpClientDefaults</c>
    /// sit above <see cref="RetryHandler"/>; a resilience handler there (for example
    /// <c>AddStandardResilienceHandler</c>) would retry requests this client deliberately sends once, such as message
    /// creation without an <c>Idempotency-Key</c>, so remove it from this client.
    /// </para>
    /// <para>
    /// <b>Time.</b> One <see cref="TimeProvider"/> from the container serves both <see cref="RetryHandler"/> (backoff,
    /// attempt timeout, <c>Retry-After</c> dates) and <see cref="NachosHttpClient"/> (the delay surfaced on exceptions).
    /// <see cref="TimeProvider.System"/> is added only when the container has none, so registering another one, before
    /// or after this call, replaces it.
    /// </para>
    /// <para>
    /// <b>Registration rule.</b> This method owns <see cref="INachosClient"/>: it removes any earlier registration (for
    /// example the in-process client of <c>AddNachos</c>), and because <c>AddNachos</c> only adds one when none exists,
    /// calling <c>AddNachos</c> afterwards does not add a second. Either order leaves exactly one
    /// <see cref="INachosClient"/>, this HTTP client; the in-process service stays resolvable by its own type. A host
    /// that serves the Nachos API must therefore not call this method, because its endpoints resolve
    /// <see cref="INachosClient"/>. Calling this method again adds <paramref name="configure"/> to the same options
    /// (applied in call order) and keeps a single pipeline and a single client registration.
    /// </para>
    /// <para>
    /// <see cref="NachosHttpClient"/> and <see cref="INachosClient"/> are transient, the usual lifetime of a client
    /// built from <see cref="IHttpClientFactory"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IHttpClientBuilder AddNachosClient(this IServiceCollection services, Action<NachosClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<NachosClientOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<NachosClientOptions>, OptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        var http = services.AddHttpClient(HttpClientName);

        // The pipeline and the client are registered once: a second RetryHandler would nest the retries (9 attempts).
        if (!services.Any(d => d.ServiceType == typeof(NachosHttpClient)))
        {
            http.ConfigureHttpClient((provider, client) => client.BaseAddress = Options(provider).BaseAddress)
                .ConfigurePrimaryHttpMessageHandler(() => new TransportRedactionHandler
                {
                    // Redirects are not followed: a 3xx is a final status (spec §16), and the redirected hop's failure
                    // text would name a host the server chose. The API gives a redirect nothing anyway: .NET strips
                    // Authorization on the redirected hop.
                    InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false },
                })
                .AddHttpMessageHandler(provider =>
                    new RetryHandler(provider.GetRequiredService<TimeProvider>(), Options(provider).AttemptTimeout));
            services.AddTransient(provider => new NachosHttpClient(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                Options(provider),
                provider.GetRequiredService<TimeProvider>()));
        }

        services.RemoveAll<INachosClient>();
        services.AddTransient<INachosClient>(provider => provider.GetRequiredService<NachosHttpClient>());
        return http;
    }

    private static NachosClientOptions Options(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<NachosClientOptions>>().Value;

    private sealed class OptionsValidator : IValidateOptions<NachosClientOptions>
    {
        public ValidateOptionsResult Validate(string? name, NachosClientOptions options) =>
            options.Validate() is { Count: > 0 } failures ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
