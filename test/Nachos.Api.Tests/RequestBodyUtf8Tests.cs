using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions;
using Nachos.Abstractions.Stores;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class RequestBodyUtf8Tests : ApiTest
{
    private static readonly string[] InvalidSequences = ["FF", "C0AF", "EDA080", "F4908080", "E282", "80"];
    private const string MessageBody = """{"messages":[{"content":"new","peer_id":"p"}]}""";
    private static readonly (string Method, string Path, string Body)[] Routes =
    [
        ("POST", "/v3/workspaces", """{"id":"new"}"""),
        ("POST", "/v3/workspaces/list", "{}"),
        ("PUT", W, """{"metadata":{"changed":true}}"""),
        ("POST", W + "/peers", """{"id":"new"}"""),
        ("POST", W + "/peers/list", "{}"),
        ("PUT", W + "/peers/p", """{"metadata":{"changed":true}}"""),
        ("POST", W + "/peers/p/sessions", "{}"),
        ("POST", W + "/sessions", """{"id":"new"}"""),
        ("POST", W + "/sessions/list", "{}"),
        ("PUT", S, """{"metadata":{"changed":true}}"""),
        ("POST", S + "/peers", """{"new-peer":{}}"""),
        ("PUT", S + "/peers", """{"new-peer":{}}"""),
        ("DELETE", S + "/peers", """["p"]"""),
        ("PUT", S + "/peers/p/config", """{"observe_me":false}"""),
        ("POST", M, MessageBody),
        ("POST", M + "/", MessageBody),
        ("POST", M + "/list", "{}"),
        ("PUT", M + "/{message_id}", """{"metadata":{"changed":true}}"""),
    ];

    public static TheoryData<string, string, string, string> AllBodyRoutes()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var (method, path, body) in Routes)
        {
            foreach (var hex in InvalidSequences)
            {
                foreach (var value in new[] { "\"@@\"", "{\"@@\":1}", "{\"nested\":[\"@@\"]}" })
                {
                    var template = method == "DELETE" ? "[\"p\"," + value + "]"
                        : path == S + "/peers" ? "{\"new-peer\":{\"future\":" + value + "}}"
                        : body[..^1] + (body.Length > 2 ? "," : "") + "\"future\":" + value + "}";
                    data.Add(method, path, template, hex);
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllBodyRoutes))]
    public async Task EveryBodyRoute_RejectsAllMalformedBytesWithoutMutationOrCachePoisoning(
        string method, string path, string template, string hex)
    {
        var observed = new ExceptionObserver();
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Insert(0, ServiceDescriptor.Singleton<IExceptionHandler>(observed))));
        using var client = factory.CreateClient();
        var messageId = await Seed(client);
        path = path.Replace("{message_id}", messageId, StringComparison.Ordinal);
        var before = await State(client);
        var isMessageCreate = method == "POST" && (path == M || path == M + "/");
        var key = isMessageCreate ? "malformed-utf8" : null;
        var response = await Raw(client, method, path, Payload(template, hex), key);
        BodyError(response);
        observed.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<RequestValidationException>().InnerException.ShouldBeNull();
        (await State(client)).ShouldBe(before);
        if (isMessageCreate)
        {
            var store = factory.Services.GetRequiredService<IMemoryStore>();
            (await store.Idempotency.TryGetAsync("w", key!, default)).ShouldBeNull();
            var valid = await Raw(client, method, path, Encoding.UTF8.GetBytes(MessageBody), key);
            valid.Status.ShouldBe(201, valid.Text);
            (await Raw(client, method, path, Encoding.UTF8.GetBytes(MessageBody), key)).ShouldBe(valid);
            var stored = await store.Idempotency.TryGetAsync("w", key!, default);
            stored.ShouldNotBeNull();
            BodyError(await Raw(client, method, path, Payload(template, hex), key));
            (await store.Idempotency.TryGetAsync("w", key!, default)).ShouldBe(stored);
            (await Raw(client, method, path, Encoding.UTF8.GetBytes(MessageBody), key)).ShouldBe(valid);
            using var page = JsonDocument.Parse((await Raw(client, "POST", M + "/list", "{}"u8.ToArray())).Text);
            page.RootElement.GetProperty("total").GetInt32().ShouldBe(2);
        }
    }

    public static TheoryData<string, string> PrecedenceBodies()
    {
        var data = new TheoryData<string, string>();
        foreach (var hex in InvalidSequences)
        {
            foreach (var template in new[]
            {
                """{"id":7,"future":"@@"}""",
                """{"id":"a b","scopes":["x"],"future":"@@"}""",
                """{"metadata":{"x":1,"x":2},"future":"@@"}""",
                """["@@"]""",
                "\"@@\"",
                "{broken JSON \"@@\"",
            })
            {
                data.Add(template, hex);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PrecedenceBodies))]
    public async Task ByteValidation_PrecedesJsonMaterializationAndSchema(string template, string hex)
    {
        var observed = new ExceptionObserver();
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Insert(0, ServiceDescriptor.Singleton<IExceptionHandler>(observed))));
        using var client = factory.CreateClient();
        await Seed(client);
        var before = await State(client);
        BodyError(await Raw(client, "POST", W + "/sessions", Payload(template, hex)));
        observed.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<RequestValidationException>().InnerException.ShouldBeNull();
        (await State(client)).ShouldBe(before);
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("C0AF")]
    [InlineData("EDA080")]
    [InlineData("F4908080")]
    [InlineData("E282")]
    [InlineData("80")]
    public async Task InvalidBytes_AfterCopyBufferBoundaryAreStillRejected(string hex)
    {
        await Seed(Client);
        var before = await State(Client);
        BodyError(await Raw(Client, "POST", "/v3/workspaces",
            Payload("{\"id\":\"new\",\"future\":\"" + new string('a', 90_000) + "@@\"}", hex)));
        (await State(Client)).ShouldBe(before);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task NonseekableChunkedBody_PreservesBomAndMultibyteValues(int chunkSize, bool bom)
    {
        await Seed(Client);
        var bytes = Payload("""{"id":"new","metadata":{"text":"@@"}}""", "C3A9F09F9880EFBFBD");
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        await using var body = new ChunkedBody(bytes, chunkSize);
        var response = await Factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/v3/workspaces";
            context.Request.Body = body;
        });
        response.Response.StatusCode.ShouldBe(200);
        body.CanSeek.ShouldBeFalse();
        body.Reads.ShouldBeGreaterThan(1);
        body.IsDisposed.ShouldBeFalse();
        var stored = await Post("/v3/workspaces", """{"id":"new"}""");
        stored.GetProperty("metadata").GetProperty("text").GetString().ShouldBe("\u00e9\U0001f600\ufffd");
    }

    [Fact]
    public async Task ValidIgnoredFields_RetainLargeBodyAndDeepContainerAcceptance()
    {
        await Seed(Client);
        var nested = new string('[', 128) + "0" + new string(']', 128);
        var body = "{\"id\":\"new\",\"future\":\"" + new string('a', 90_000) + "\",\"deep\":" + nested + "}";
        var response = await Raw(Client, "POST", "/v3/workspaces", Encoding.UTF8.GetBytes(body));
        response.Status.ShouldBe(200, response.Text);
        using var document = JsonDocument.Parse(response.Text);
        document.RootElement.GetProperty("id").GetString().ShouldBe("new");
        Ids(await Post("/v3/workspaces/list")).ShouldContain("new");
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    public async Task BodyReadFailure_AfterMalformedPrefixRemainsServerError(string failure)
    {
        var observed = new ExceptionObserver();
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Insert(0, ServiceDescriptor.Singleton<IExceptionHandler>(observed))));
        using var client = factory.CreateClient();
        await Seed(client);
        var before = await State(client);
        Exception error = failure == "io" ? new IOException("private read failure") : new ObjectDisposedException("private body");
        await using var body = new ChunkedBody([0xFF], 1, error);
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/v3/workspaces";
            context.Request.Body = body;
        });
        response.Response.StatusCode.ShouldBe(500);
        observed.Exceptions.ShouldHaveSingleItem().ShouldBeSameAs(error);
        using var reader = new StreamReader(response.Response.Body);
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
        Problem(json.RootElement, 500, JsonValueKind.String);
        json.RootElement.GetProperty("detail").GetString().ShouldBe("An unexpected error occurred.");
        (await State(client)).ShouldBe(before);
    }

    [Fact]
    public async Task Cancellation_DuringMalformedBodyReadIsNotJsonFailure()
    {
        await Seed(Client);
        var before = await State(Client);
        await using var body = new ChunkedBody([0xFF], 1, waitForCancellation: true);
        using var abort = new CancellationTokenSource();
        var pending = Factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/v3/workspaces";
            context.Request.Body = body;
        }, abort.Token);
        (await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10))).CanBeCanceled.ShouldBeTrue();
        await abort.CancelAsync();
        await body.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Response.StatusCode.ShouldBe(499);
        (await State(Client)).ShouldBe(before);
    }

    [Fact]
    public async Task ValidBomAndUnicode_ReplayPreservesCapturedBytesAndNumericIdentity()
    {
        await Seed(Client);
        var body = Payload("""{"messages":[{"content":"@@","peer_id":"p"}],"z":1e2,"a":1,"a":2}""", "C3A9F09F9880EFBFBD");
        var first = await RawBytes(Client, "POST", M, [0xEF, 0xBB, 0xBF, .. body], "unicode");
        first.Status.ShouldBe(201, Encoding.UTF8.GetString(first.Bytes));
        using var document = JsonDocument.Parse(first.Bytes);
        document.RootElement[0].GetProperty("content").GetString().ShouldBe("\u00e9\U0001f600\ufffd");
        await Send(HttpMethod.Put, M + "/" + document.RootElement[0].GetProperty("id").GetString(),
            """{"metadata":{"changed":true}}""");
        var reordered = Payload(" {\"a\":1,\"z\":1e2,\"messages\":[{\"peer_id\":\"p\",\"content\":\"@@\"}],\"a\":2} \n",
            "C3A9F09F9880EFBFBD");
        var replay = await RawBytes(Client, "POST", M, reordered, "unicode");
        replay.Status.ShouldBe(201);
        replay.Bytes.ShouldBe(first.Bytes);
        var stored = await Factory.Services.GetRequiredService<IMemoryStore>().Idempotency.TryGetAsync("w", "unicode", default);
        Encoding.UTF8.GetBytes(stored.ShouldNotBeNull().ResponseBody).ShouldBe(first.Bytes);
        var different = Payload("""{"messages":[{"content":"@@","peer_id":"p"}],"z":100,"a":1,"a":2}""", "C3A9F09F9880EFBFBD");
        var conflict = await Raw(Client, "POST", M, different, "unicode");
        conflict.Status.ShouldBe(422);
        using var problem = JsonDocument.Parse(conflict.Text);
        problem.RootElement.GetProperty("type").GetString().ShouldBe("urn:nachos:problem:idempotency-key-reused");
        Ids(await Post(M + "/list")).Length.ShouldBe(2);
    }

    private static void BodyError((int Status, string Text) response)
    {
        response.Status.ShouldBe(422, response.Text);
        using var document = JsonDocument.Parse(response.Text);
        Location(document.RootElement, "body");
        var detail = document.RootElement.GetProperty("detail");
        detail.GetArrayLength().ShouldBe(1);
        detail[0].GetProperty("type").GetString().ShouldBe("json_invalid");
        detail[0].GetProperty("msg").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    private static byte[] Payload(string template, string hex)
    {
        var parts = template.Split("@@", StringSplitOptions.None);
        parts.Length.ShouldBe(2);
        return [.. Encoding.ASCII.GetBytes(parts[0]), .. Convert.FromHexString(hex), .. Encoding.ASCII.GetBytes(parts[1])];
    }

    private static async Task<string> Seed(HttpClient client)
    {
        (await Raw(client, "POST", "/v3/workspaces", """{"id":"w"}"""u8.ToArray())).Status.ShouldBe(200);
        (await Raw(client, "POST", W + "/sessions", """{"id":"s","peers":{"p":{"observe_me":true}}}"""u8.ToArray())).Status.ShouldBe(200);
        var message = await Raw(client, "POST", M, """{"messages":[{"content":"seed","peer_id":"p"}]}"""u8.ToArray());
        message.Status.ShouldBe(201);
        using var document = JsonDocument.Parse(message.Text);
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    private static async Task<string[]> State(HttpClient client)
    {
        List<string> state = [];
        foreach (var (method, path) in new[]
        {
            ("POST", "/v3/workspaces/list"), ("POST", W + "/sessions/list"), ("POST", W + "/peers/list"),
            ("GET", S + "/peers"), ("GET", S + "/peers/p/config"), ("POST", M + "/list"),
        })
        {
            var response = await Raw(client, method, path, "{}"u8.ToArray());
            response.Status.ShouldBe(200, response.Text);
            state.Add(response.Text);
        }
        return state.ToArray();
    }

    private static async Task<(int Status, string Text)> Raw(HttpClient client, string method, string path, byte[] bytes, string? key = null)
    {
        var response = await RawBytes(client, method, path, bytes, key);
        return (response.Status, Encoding.UTF8.GetString(response.Bytes));
    }

    private static async Task<(int Status, byte[] Bytes)> RawBytes(
        HttpClient client, string method, string path, byte[] bytes, string? key = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    private sealed class ExceptionObserver : IExceptionHandler
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = new();
        public ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            Exceptions.Enqueue(exception);
            return ValueTask.FromResult(false);
        }
    }

    private sealed class ChunkedBody(byte[] bytes, int chunkSize, Exception? failure = null, bool waitForCancellation = false) : Stream
    {
        private int _offset;
        public int Reads { get; private set; }
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource<CancellationToken> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (_offset == bytes.Length)
            {
                if (failure is not null) throw failure;
                if (waitForCancellation)
                {
                    Waiting.TrySetResult(cancellationToken);
                    try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
                    catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
                }
            }
            var count = Math.Min(Math.Min(chunkSize, buffer.Length), bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
