using System.Net;
using System.Text;

namespace Nachos.Client.Tests;

/// <summary>One request as the innermost handler saw it (the body is read eagerly, before any retry).</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    string? ContentType,
    string? ContentTypeHeader,
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

    /// <summary>
    /// A response whose headers arrive but whose body fails part-way through, as when the connection resets after
    /// the server committed.
    /// </summary>
    public static HttpResponseMessage BrokenBody(HttpStatusCode status, BrokenStream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        request.Options.TryGetValue(RetryHandler.RouteTemplate, out var template);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content?.Headers.ContentType?.ToString(),
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

/// <summary>Yields a few bytes, then fails like a reset connection. Records whether it was disposed.</summary>
internal sealed class BrokenStream : Stream
{
    private bool _sentPrefix;

    public bool Disposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!_sentPrefix)
        {
            _sentPrefix = true;
            buffer[0] = (byte)'[';
            return 1;
        }

        throw new IOException("connection reset while reading the response body");
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>JSON content that records when it is disposed.</summary>
internal sealed class TrackedContent(string json) : StringContent(json, Encoding.UTF8, "application/json")
{
    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
