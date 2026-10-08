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
    /// <summary>Requests past this count fail the test instead of letting a runaway retry loop spin forever.</summary>
    public const int RunawayLimit = 20;

    private readonly List<RecordedRequest> _requests = [];
    private readonly List<HttpRequestMessage> _messages = [];

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

    /// <summary>The request objects as received, for disposal checks after the call.</summary>
    public IReadOnlyList<HttpRequestMessage> Messages
    {
        get
        {
            lock (_requests)
            {
                return [.. _messages];
            }
        }
    }

    /// <summary>
    /// True when the request's content was disposed (disposing a request disposes its content). Only meaningful for
    /// requests with a body.
    /// </summary>
    public static async Task<bool> IsDisposedAsync(HttpRequestMessage request)
    {
        try
        {
            _ = await request.Content!.ReadAsByteArrayAsync();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
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
            _messages.Add(request);
            attempt = _requests.Count;
        }

        if (attempt > RunawayLimit)
        {
            throw new InvalidOperationException($"Runaway retry loop: {attempt} requests.");
        }

        return Respond(recorded, attempt);
    }
}

/// <summary>
/// Yields a few bytes, then fails like a reset connection (running <paramref name="beforeFailure"/> first).
/// Records whether it was disposed.
/// </summary>
internal sealed class BrokenStream(Action? beforeFailure = null) : Stream
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

        beforeFailure?.Invoke();
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

/// <summary>
/// Yields a few bytes, then stalls until the read's own cancellation token fires (calling
/// <paramref name="onStall"/> as it starts waiting). A 5 s safety net turns a read that is never cancelled into an
/// IOException so a broken implementation fails the test instead of hanging it.
/// </summary>
internal sealed class StallingStream(Action onStall) : Stream
{
    private bool _sentPrefix;

    public bool Disposed { get; private set; }

    /// <summary>True when the stall ended because the token passed to the read was cancelled.</summary>
    public bool ReadWasCancelled { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async reads only");

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_sentPrefix)
        {
            _sentPrefix = true;
            buffer.Span[0] = (byte)'[';
            return 1;
        }

        onStall();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ReadWasCancelled = true;
            throw;
        }

        throw new IOException("stalled read was never cancelled");
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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

/// <summary>A readable, non-seekable stream over fixed bytes, so the content length is unknown to HttpClient.</summary>
internal sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
}
