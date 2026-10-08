using System.Net;
using System.Text;

namespace Nachos.Client.Tests;

/// <summary>One request as the innermost handler saw it (the body is read eagerly, before any retry).</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    string? ContentType,
    string? RouteTemplate,
    string? IdempotencyKey,
    string? Authorization);

/// <summary>
/// An in-test fake server: records every request it receives and answers through <see cref="Respond"/>, which gets
/// the request and its 1-based attempt number. No sockets are involved.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public StubHandler(Func<RecordedRequest, int, HttpResponseMessage> respond)
    {
        Respond = respond;
    }

    public Func<RecordedRequest, int, HttpResponseMessage> Respond { get; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        request.Options.TryGetValue(RetryHandler.RouteTemplate, out var template);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            body,
            request.Content?.Headers.ContentType?.MediaType,
            template,
            request.Headers.TryGetValues("Idempotency-Key", out var keys) ? string.Join(",", keys) : null,
            request.Headers.Authorization?.ToString());
        int attempt;
        lock (_requests)
        {
            _requests.Add(recorded);
            attempt = _requests.Count;
        }

        return Respond(recorded, attempt);
    }
}
