using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
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
    /// The primary handler is a <see cref="SocketsHttpHandler"/>, and when the handler chain is built, after every
    /// configuration of it on the returned builder has run, a filter of this client finishes it: a primary handler that
    /// is a <see cref="SocketsHttpHandler"/> (this one, one of your own from <c>ConfigurePrimaryHttpMessageHandler</c>,
    /// or the one <c>UseSocketsHttpHandler</c> keeps or creates) gets <c>AllowAutoRedirect</c> set to false, whatever
    /// your configuration set it to; and the primary handler, whatever its type, is wrapped in the redaction handler
    /// described below, once. So <c>UseSocketsHttpHandler((handler, services) => handler.PooledConnectionLifetime = ...)</c>
    /// and <c>ConfigurePrimaryHttpMessageHandler((handler, services) => ((SocketsHttpHandler)handler).PooledConnectionLifetime = ...)</c>
    /// tune the handler this method registered (the delegate receives the <see cref="SocketsHttpHandler"/>, not the
    /// wrapper), and <c>ConfigurePrimaryHttpMessageHandler(() => ...)</c> replaces it under the same rules.
    /// </para>
    /// <para>
    /// Redirects are never followed: a 3xx is a final status under spec §16 status precedence and surfaces as the
    /// mapped <see cref="HttpRequestException"/> with that status, sent once. Following one would let the server pick
    /// the next hop's host, which .NET names in a connection failure's text and in <c>System.Net.NameResolution</c>
    /// events; and .NET drops <c>Authorization</c> on the redirected hop, so an authenticated API gains nothing from
    /// redirects. The rule holds for a client without credentials too: an <c>http</c> base address that the server
    /// answers with a 301 or 308 to <c>https</c> fails with that status instead of being upgraded, so configure the
    /// <c>https</c> address. A caller who needs redirects must supply a primary handler that is not a
    /// <see cref="SocketsHttpHandler"/> (an <see cref="HttpClientHandler"/>, or a handler of their own over one) and
    /// accepts that its connection failures and resolution events name hosts the server chose.
    /// </para>
    /// <para>
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
    /// choose that host). A TLS failure is among the replaced ones: it surfaces as the fixed text naming
    /// <see cref="HttpRequestError.SecureConnectionError"/>, without the reason (an untrusted root, a name mismatch),
    /// which the <c>System.Net.Security</c> EventSource still reports. <see cref="RetryHandler"/> and
    /// <see cref="NachosHttpClient"/> apply this whatever the primary handler (see <c>SecretRedaction</c>);
    /// </description></item>
    /// <item><description>
    /// the <see cref="IHttpClientFactory"/> logs: this client has none. The factory's built-in <c>LogicalHandler</c>
    /// and <c>ClientHandler</c> loggers write every request and response header at Trace, and only their formatted
    /// text replaces the values with <c>*</c>: the structured state they hand providers (what OpenTelemetry, Serilog
    /// or Application Insights export) carries the raw values, the request's own <c>Authorization</c> on every call
    /// included, and whatever a server echoes into <c>Location</c>, <c>WWW-Authenticate</c>, <c>Retry-After</c>, a
    /// charset or any other value. So this method removes them (<c>RemoveAllLoggers</c>). To log this client's
    /// traffic, add a logger of your own with <c>AddLogger&lt;T&gt;</c> on the returned builder, and do not log
    /// headers in it. Adding the built-in ones back with <c>AddDefaultLogger</c> puts the header values into the
    /// structured state again, which nothing here covers. What the client does for any logger or handler above the
    /// primary handler: that handler, whatever you made it (see Pipeline), is wrapped in a handler that applies the
    /// exception rule above before anything above it sees a failure, and that removes a response header whose name
    /// shares 16 characters with the bearer value, in any letter case (a bare JWT is a valid header name; a server
    /// that reflects only part of the value, or another case of it, is caught the same way, at the cost of a real
    /// header whose name happens to share such a run). A handler you add sits above that wrapper and below
    /// <see cref="RetryHandler"/>, which cleans what it throws;
    /// </description></item>
    /// <item><description>
    /// server text the client shows on purpose (error bodies and reason phrases mapped to exceptions): the bearer value
    /// is redacted as plain text (in any letter case, for a value of at least 8 characters) and as hex.
    /// </description></item>
    /// </list>
    /// Not covered:
    /// <list type="bullet">
    /// <item><description>
    /// <c>System.Net.Http</c> diagnostics raised by the framework itself, before any of this code runs: the
    /// <c>System.Net.Http</c> EventSource (for example <c>RequestFailedDetailed</c>, which carries the raw exception text,
    /// including a body that <see cref="HttpClient"/> buffers for a never-retried request), and activity exception
    /// events recorded by tracing, which carry the raw text of a failure in the header phase. Do not enable them at
    /// verbose levels against a server you do not trust;
    /// </description></item>
    /// <item><description>
    /// diagnostics that carry the outgoing request itself, whatever the server does: the <c>DiagnosticSource</c>
    /// events <c>System.Net.Http.HttpRequestOut.Start</c>, <c>System.Net.Http.HttpRequestOut.Stop</c>,
    /// <c>System.Net.Http.Request</c>, <c>System.Net.Http.Response</c> (through
    /// <see cref="HttpResponseMessage.RequestMessage"/>, and with every response header value the server sent,
    /// <c>WWW-Authenticate</c> included) and <c>System.Net.Http.Exception</c> hand listeners the
    /// <see cref="HttpRequestMessage"/>, whose <c>ToString</c> prints the <c>Authorization</c> value; the
    /// <c>Private.InternalDiagnostics.System.Net.Http</c> EventSource prints it the same way, and the
    /// <c>Private.InternalDiagnostics.System.Net.Sockets</c> EventSource's <c>DumpBuffer</c> event (Verbose) holds the
    /// raw request bytes, <c>Authorization</c> included. A listener on any of those sees the bearer value on every
    /// request: treat every <c>DiagnosticSource</c> and EventSource consumer as sensitive;
    /// </description></item>
    /// <item><description>
    /// <c>System.Net.NameResolution</c> events, which name the host being resolved: with your own primary handler that
    /// follows redirects, that is the host the server chose (the default pipeline follows none, see Pipeline);
    /// </description></item>
    /// <item><description>what your own handlers and loggers log;</description></item>
    /// <item><description>
    /// in mapped server text, echoes in another encoding (percent-encoding, base64), another letter case of a value
    /// shorter than 8 characters (longer ones are matched in any case), or only part of the value, including a value
    /// the server splits by inserting the literal marker <c>[redacted]</c> into it (the marker is never matched
    /// inside, so the pieces around it are shown); and non-string <see cref="Exception.Data"/> values (a
    /// <c>string[]</c>, a <see cref="Uri"/>).
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Resilience handlers.</b> Handlers added to every client by <c>ConfigureHttpClientDefaults</c> sit above
    /// <see cref="RetryHandler"/>. A <c>Microsoft.Extensions.Http.Resilience</c> <c>ResilienceHandler</c> there (what
    /// <c>AddStandardResilienceHandler</c> in a service-defaults project adds) retries by status alone, so it would
    /// resend what this client sends once by spec (a key creation, a 501), attempt a message create (always keyed, so
    /// already retried up to three attempts) up to twelve times, cut and retry even a mutation on its own per-attempt
    /// timeout, and override <see cref="NachosClientOptions.AttemptTimeout"/> and <c>Retry-After</c> with its total
    /// timeout. The client does its own, spec-defined retries, so that handler is removed from this client's chain
    /// when it is built, whether the defaults were configured before or after this call: it is matched by its exact
    /// type full name, <c>Microsoft.Extensions.Http.Resilience.ResilienceHandler</c> (package 8.2.0 and later) or
    /// <c>Microsoft.Extensions.Http.Resilience.Internal.ResilienceHandler</c> (8.0.0 and 8.1.0), and every other
    /// handler you add stays, whatever its name. A caller who wants resilience of their own replaces
    /// <see cref="RetryHandler"/>'s semantics knowingly, with a handler of another type.
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

        // The factory's built-in loggers hand providers every header value as structured state, the request's own
        // Authorization included; only their formatted text is redacted. This client has no factory loggers.
        http.RemoveAllLoggers();

        // The filter finishes the primary handler when the chain is built, after the caller's configuration of it:
        // no redirects on a SocketsHttpHandler, and the redaction wrapper around whatever the primary handler is.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, TransportRedactionFilter>());

        // The pipeline and the client are registered once: a second RetryHandler would nest the retries (9 attempts).
        if (!services.Any(d => d.ServiceType == typeof(NachosHttpClient)))
        {
            http.ConfigureHttpClient((provider, client) => client.BaseAddress = Options(provider).BaseAddress)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler())
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
