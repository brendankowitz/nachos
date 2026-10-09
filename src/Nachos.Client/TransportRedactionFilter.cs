using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Nachos.Client;

/// <summary>
/// Finishes the primary handler of the <c>AddNachosClient</c> pipeline after every configuration of it has run: the
/// primary handler never follows redirects when it is a <see cref="SocketsHttpHandler"/>, and it is wrapped in
/// <see cref="TransportRedactionHandler"/> exactly once, whatever the caller made it.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IHttpMessageHandlerBuilderFilter"/> runs when the factory builds the handler chain, after the
/// <c>ConfigurePrimaryHttpMessageHandler</c>, <c>UseSocketsHttpHandler</c> and <c>AddHttpMessageHandler</c> calls of
/// every builder of the named client, so it sees the primary handler those calls ended with. Wrapping it there, not in
/// <c>AddNachosClient</c>'s own primary-handler configuration, keeps the wrapper when a caller replaces or replaces
/// again the primary handler, and keeps a caller's <c>ConfigurePrimaryHttpMessageHandler((handler, services) =>
/// ...)</c> delegate working on the <see cref="SocketsHttpHandler"/> itself, not on the wrapper.
/// </para>
/// <para>
/// Filters are not scoped to a client, so this one acts only on the builder named
/// <see cref="NachosClientServiceCollectionExtensions.HttpClientName"/>. It is registered once per container
/// (<c>TryAddEnumerable</c>), so a container that calls <c>AddNachosClient</c> twice still wraps once; a primary
/// handler that already is a <see cref="TransportRedactionHandler"/> is left alone for the same reason.
/// </para>
/// <para>
/// It also removes any <c>Microsoft.Extensions.Http.Resilience.ResilienceHandler</c> from the chain (matched by type
/// name, so no package is referenced and any version of it counts). <c>AddStandardResilienceHandler</c> in
/// <c>ConfigureHttpClientDefaults</c>, the shape a service-defaults project generates, puts one above
/// <see cref="RetryHandler"/> on every named client, and it retries by status alone: a request this client sends once
/// by spec (a key creation, an unkeyed message create, a 501) is resent, a keyed create is attempted up to twelve
/// times, its own per-attempt timeout cuts and retries even a mutation, and its total timeout overrides
/// <see cref="NachosClientOptions.AttemptTimeout"/> and <c>Retry-After</c>. The client does its own, spec-defined
/// retries, so a second retry layer is removed; every other handler the caller adds stays. A caller who wants
/// resilience of their own replaces <see cref="RetryHandler"/>'s semantics knowingly, with a handler of another type.
/// </para>
/// </remarks>
internal sealed class TransportRedactionFilter : IHttpMessageHandlerBuilderFilter
{
    private const string ResilienceHandlerTypeName = "Microsoft.Extensions.Http.Resilience.ResilienceHandler";

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return builder =>
        {
            next(builder);
            if (builder.Name != NachosClientServiceCollectionExtensions.HttpClientName)
            {
                return;
            }

            for (var i = builder.AdditionalHandlers.Count - 1; i >= 0; i--)
            {
                if (builder.AdditionalHandlers[i].GetType().FullName == ResilienceHandlerTypeName)
                {
                    builder.AdditionalHandlers.RemoveAt(i);
                }
            }

            // Redirects are not followed: a 3xx is a final status (spec §16), and the redirected hop's failure text
            // would name a host the server chose. The API gives a redirect nothing anyway: .NET strips Authorization
            // on the redirected hop. A caller who wants them must supply a primary handler of another type.
            if (builder.PrimaryHandler is SocketsHttpHandler sockets)
            {
                sockets.AllowAutoRedirect = false;
            }

            if (builder.PrimaryHandler is not TransportRedactionHandler)
            {
                builder.PrimaryHandler = new TransportRedactionHandler { InnerHandler = builder.PrimaryHandler };
            }
        };
    }
}
