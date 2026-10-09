namespace Nachos.Client;

/// <summary>
/// Wraps the primary handler that <c>AddNachosClient</c> registers, so a transport failure whose text echoes the
/// request's bearer value is redacted (<see cref="SecretRedaction"/>) before anything above it sees it, including the
/// <see cref="IHttpClientFactory"/> <c>ClientHandler</c> logger, which sits directly above the primary handler.
/// </summary>
/// <remarks>
/// It only rewrites exceptions; requests and responses pass through untouched. Failures while a caller later reads a
/// response body do not pass through any handler; <see cref="RetryHandler"/> and <see cref="NachosHttpClient"/> redact
/// those.
/// </remarks>
internal sealed class TransportRedactionHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var secrets = RedactionSecrets.FromAuthorization(request);
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SecretRedaction.Mentions(ex, secrets))
        {
            throw SecretRedaction.Redact(ex, secrets);
        }
    }
}
