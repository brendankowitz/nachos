using System.Net.Http.Headers;

namespace Nachos.Client;

/// <summary>
/// Wraps the primary handler of the <c>AddNachosClient</c> pipeline (<see cref="TransportRedactionFilter"/> puts it
/// there), so that nothing above it (a handler or logger the caller adds, the factory's built-in loggers if the caller
/// adds them back) sees transport text that can repeat what the server sent.
/// </summary>
/// <remarks>
/// <para>
/// A failure goes through <see cref="SecretRedaction.Sanitize"/>: known-safe connection failures are kept, everything
/// else is replaced by fixed text. A successful response passes through, except that a response header whose name
/// echoes the request's bearer value is removed: a name that shares a run of
/// <see cref="RedactionSecrets.MinHeaderNameMatchLength"/> characters with the value in any letter case
/// (<see cref="RedactionSecrets.HeaderNameEchoes"/>), or that holds a whole hex form of it. Such a name is valid HTTP
/// when the value is a bare JWT, or any part of one, and a logger above would otherwise write it. A value shorter than
/// that run is never matched against names. Header values are not inspected: the factory's built-in loggers, the only
/// thing in the pipeline that wrote them, are removed by <c>AddNachosClient</c>.
/// </para>
/// <para>
/// Failures while a caller later reads a response body do not pass through any handler; <see cref="RetryHandler"/> and
/// <see cref="NachosHttpClient"/> apply the same rule to those.
/// </para>
/// <para>
/// Cost: the secrets (the value and its hex forms, several times its length) are built on the failure path, and on the
/// success path only for a response header name long enough to hold a hex form. The bearer value itself is read only
/// when a name is at least <see cref="RedactionSecrets.MinHeaderNameMatchLength"/> long (<c>Transfer-Encoding</c> is),
/// and then without formatting it, so a call allocates nothing here for a request whose header is parsed; a retry copy
/// stores the raw header text and parses it once in that case (see <see cref="RedactionSecrets.FromAuthorization"/>).
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

    // The bearer value is read on the first name long enough to echo it (empty when the request has none to match),
    // and its hex forms are built on the first name long enough to hold one: contiguous hex is twice the value's
    // length, so ordinary names never reach that.
    private static void RemoveEchoedHeaders(HttpRequestMessage request, HttpResponseMessage response)
    {
        string? bearer = null;
        RedactionSecrets? hexForms = null;
        Remove(response.Headers);
        Remove(response.Content.Headers);

        void Remove(HttpHeaders headers)
        {
            List<string>? echoed = null;
            foreach (var header in headers.NonValidated)
            {
                var name = header.Key;
                if (name.Length < RedactionSecrets.MinHeaderNameMatchLength)
                {
                    continue;
                }

                bearer ??= RedactionSecrets.BearerForHeaderNames(request) ?? string.Empty;
                if (bearer.Length == 0)
                {
                    return;
                }

                if (RedactionSecrets.HeaderNameEchoes(name, bearer) ||
                    (name.Length >= 2 * bearer.Length && (hexForms ??= RedactionSecrets.Of(bearer)).OccursIn(name)))
                {
                    (echoed ??= []).Add(name);
                }
            }

            if (echoed is null)
            {
                return;
            }

            foreach (var name in echoed)
            {
                headers.Remove(name);
            }
        }
    }
}
