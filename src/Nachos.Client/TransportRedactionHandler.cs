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
/// the request's bearer value (plain or hex, <see cref="RedactionSecrets.ForHeaderNames"/>, so only a value of at least
/// <see cref="RedactionSecrets.MinHeaderNameMatchLength"/> characters) is removed: such a name is valid HTTP when the
/// value is a bare JWT, and the factory would otherwise log it.
/// </para>
/// <para>
/// Failures while a caller later reads a response body do not pass through any handler; <see cref="RetryHandler"/> and
/// <see cref="NachosHttpClient"/> apply the same rule to those.
/// </para>
/// <para>
/// Cost: the secrets are built on the failure path, and for header names only when a name is at least as long as the
/// shortest matchable bearer value; the success path of a call with a JWT allocates nothing for them.
/// </para>
/// </remarks>
internal sealed class TransportRedactionHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safe = SecretRedaction.Sanitize(ex, RedactionSecrets.FromAuthorization(request));
            if (ReferenceEquals(safe, ex))
            {
                throw;
            }

            throw safe;
        }

        RemoveEchoedHeaders(request, response);
        return response;
    }

    // A header name can hold the bearer value only when it is at least as long, so the secrets (the value and its hex
    // forms, several times its length) are built only once such a name arrives: a successful call with a JWT, whose
    // names are all shorter, allocates nothing here.
    private static void RemoveEchoedHeaders(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (RedactionSecrets.ShortestHeaderNameMatch(request) is not { } minLength)
        {
            return;
        }

        RedactionSecrets? secrets = null;
        Remove(response.Headers);
        Remove(response.Content.Headers);

        void Remove(HttpHeaders headers)
        {
            List<string>? echoed = null;
            foreach (var header in headers.NonValidated)
            {
                if (header.Key.Length >= minLength && (secrets ??= RedactionSecrets.ForHeaderNames(request)).OccursIn(header.Key))
                {
                    (echoed ??= []).Add(header.Key);
                }
            }

            foreach (var name in echoed ?? [])
            {
                headers.Remove(name);
            }
        }
    }
}
