using System.Net.Http.Headers;

namespace Nachos.Client;

/// <summary>
/// Wraps the primary handler that <c>AddNachosClient</c> registers, so that nothing above it, including the
/// <see cref="IHttpClientFactory"/> <c>ClientHandler</c> and <c>LogicalHandler</c> loggers, sees transport text that can
/// repeat what the server sent.
/// </summary>
/// <remarks>
/// <para>
/// A failure goes through <see cref="SecretRedaction.Sanitize"/>: known-safe connection failures are kept, everything
/// else is replaced by fixed text. A successful response passes through, except that a response header whose name holds
/// the request's bearer value (plain or hex, <see cref="RedactionSecrets"/>) is removed: such a name is valid HTTP when
/// the value is a bare JWT, and the factory would otherwise log it.
/// </para>
/// <para>
/// Failures while a caller later reads a response body do not pass through any handler; <see cref="RetryHandler"/> and
/// <see cref="NachosHttpClient"/> apply the same rule to those.
/// </para>
/// </remarks>
internal sealed class TransportRedactionHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var secrets = RedactionSecrets.FromAuthorization(request);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safe = SecretRedaction.Sanitize(ex, secrets);
            if (ReferenceEquals(safe, ex))
            {
                throw;
            }

            throw safe;
        }

        RemoveEchoedHeaders(response.Headers, secrets);
        RemoveEchoedHeaders(response.Content.Headers, secrets);
        return response;
    }

    private static void RemoveEchoedHeaders(HttpHeaders headers, RedactionSecrets secrets)
    {
        if (secrets.IsEmpty)
        {
            return;
        }

        foreach (var name in headers.NonValidated.Select(header => header.Key).Where(secrets.OccursIn).ToList())
        {
            headers.Remove(name);
        }
    }
}
