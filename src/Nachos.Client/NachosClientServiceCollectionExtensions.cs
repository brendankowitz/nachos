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
    /// <see cref="RetryHandler"/> gets <see cref="NachosClientOptions.AttemptTimeout"/>. <see cref="HttpClient.Timeout"/>
    /// keeps its default of 100 s and stays the overall bound of a call, retries and backoff included; change it with
    /// <c>ConfigureHttpClient</c> on the returned builder. <see cref="IHttpClientFactory"/> logging redacts header
    /// values by default, so the <c>Authorization</c> value never reaches the logs; do not turn that redaction off for
    /// this client. Handlers added to every client by <c>ConfigureHttpClientDefaults</c>
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
