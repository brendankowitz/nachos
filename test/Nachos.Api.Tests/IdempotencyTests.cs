using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Api.Tests.Auth;
using Shouldly;
using Xunit.Abstractions;

namespace Nachos.Api.Tests;

public sealed class IdempotencyTests(ITestOutputHelper output)
{
    private const string Messages = "/v3/workspaces/A/sessions/s1/messages";
    private const string Body = """{"messages":[{"content":"caf\u00e9 \ud83d\ude00","peer_id":"p1"}],"future":1e2}""";

    [Theory]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("", true)]
    [InlineData("/", true)]
    public async Task MultipleValues_AreLocatedValueFreeErrors_WithoutMutationOrCachePoisoning(string suffix, bool identical)
    {
        using var host = new AuthHost();
        await host.Seed();
        string[] keys = ["private-first", identical ? "private-first" : "private-second"];
        var before = await State(host);
        var result = await Send(host, Messages + suffix, Body, keys);
        Validation(result, "value_error", "header", "idempotency-key");
        foreach (var key in keys) Encoding.UTF8.GetString(result.Bytes).ShouldNotContain(key);
        (await State(host)).ShouldBe(before);
        foreach (var key in keys.Append(string.Join(',', keys)).Append(string.Join(", ", keys)))
            (await host.Store.Idempotency.TryGetAsync("A", key, default)).ShouldBeNull();
        var literal = string.Join(',', keys);
        var first = await Send(host, Messages, Body, [literal]);
        first.Status.ShouldBe(201);
        (await host.Store.Idempotency.TryGetAsync("A", literal, default)).ShouldNotBeNull();
        (await Send(host, Messages + "/", Body, [literal])).Bytes.ShouldBe(first.Bytes);
        await Count(host, 1);
    }

    [Theory]
    [InlineData("Case-Key", "case-key")]
    [InlineData("a,b", "a, b")]
    [InlineData(" a ", "a")]
    [InlineData("a\tb", "ab")]
    [InlineData("a\u0001b", "ab")]
    [InlineData("a\u007fb", "ab")]
    public async Task OneValue_IsPreservedExactly_AndNotNormalized(string key, string other)
    {
        using var host = new AuthHost();
        await host.Seed();
        var first = await Boundary(host, Body, [key]);
        first.Status.ShouldBe(201, Encoding.UTF8.GetString(first.Bytes));
        var record = (await host.Store.Idempotency.TryGetAsync("A", key, default)).ShouldNotBeNull();
        record.Key.ShouldBe(key);
        Encoding.UTF8.GetBytes(record.ResponseBody).ShouldBe(first.Bytes);
        (await host.Store.Idempotency.TryGetAsync("A", other, default)).ShouldBeNull();
        (await Boundary(host, Body, [key])).Bytes.ShouldBe(first.Bytes);
        (await Boundary(host, Body, [other])).Status.ShouldBe(201);
        await Count(host, 2);
    }

    [Theory]
    [InlineData(0, false, 422)]
    [InlineData(255, false, 201)]
    [InlineData(256, false, 422)]
    [InlineData(1, true, 422)]
    public async Task ContentValidation_RemainsCoreAsciiAndLengthRule(int length, bool nonAscii, int status)
    {
        using var host = new AuthHost();
        await host.Seed();
        var key = nonAscii ? "\u00e9" : new string('a', length);
        var response = await Boundary(host, Body, [key]);
        response.Status.ShouldBe(status, Encoding.UTF8.GetString(response.Bytes));
        if (status == 422)
        {
            using var json = JsonDocument.Parse(response.Bytes);
            json.RootElement.GetProperty("detail").ValueKind.ShouldBe(JsonValueKind.String);
            (await host.Store.Idempotency.TryGetAsync("A", key, default)).ShouldBeNull();
        }
        await Count(host, status == 201 ? 1 : 0);
    }

    [Fact]
    public async Task AbsentKey_AppendsEachRequest()
    {
        using var host = new AuthHost();
        await host.Seed();
        var first = await Send(host, Messages, Body, []);
        var second = await Send(host, Messages + "/", Body, []);
        first.Status.ShouldBe(201);
        second.Status.ShouldBe(201);
        second.Bytes.ShouldNotBe(first.Bytes);
        await Count(host, 2);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("", true)]
    [InlineData("/", true)]
    public async Task SameKey_ReplaysExactCapturedStatusAndUtf8_AfterLiveMetadataUpdate(string suffix, bool useEntra)
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra };
        await host.Seed();
        await host.Store.Grants.AddAsync(new("object-id", "A", GrantRoles.Workspace), default);
        var token = useEntra ? entra.Token() : AuthHost.Key("workspace");
        var first = await Send(host, Messages + suffix, Body, ["captured"], token);
        first.Status.ShouldBe(201);
        var record = (await host.Store.Idempotency.TryGetAsync("A", "captured", default)).ShouldNotBeNull();
        using var json = JsonDocument.Parse(first.Bytes);
        var id = json.RootElement[0].GetProperty("id").GetString()!;
        json.RootElement[0].GetProperty("content").GetString().ShouldBe("caf\u00e9 \U0001f600");
        var changed = await host.Send("PUT", Messages + "/" + id, """{"metadata":{"changed":true}}""", token, 200);
        changed.GetProperty("metadata").GetProperty("changed").GetBoolean().ShouldBeTrue();
        var replay = await Send(host, Messages + (suffix.Length == 0 ? "/" : ""), Body, ["captured"], token);
        replay.Status.ShouldBe(first.Status);
        replay.Status.ShouldBe(record.ResponseStatus);
        replay.Bytes.ShouldBe(first.Bytes);
        replay.Bytes.ShouldBe(Encoding.UTF8.GetBytes(record.ResponseBody));
        (await host.Store.Idempotency.TryGetAsync("A", "captured", default)).ShouldBe(record);
        await Count(host, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentBodyOrSession_ReturnsReusedProblem_WithoutMutation(bool otherSession)
    {
        using var host = new AuthHost();
        await host.Seed();
        await host.Send("POST", "/v3/workspaces/A/sessions", """{"id":"s2"}""", AuthHost.Key("workspace"), 200);
        (await Send(host, Messages, Body, ["reused"])).Status.ShouldBe(201);
        var before = await State(host);
        Reused(await Send(host, otherSession ? Messages.Replace("s1", "s2", StringComparison.Ordinal) : Messages,
            otherSession ? Body : Body.Replace("1e2", "100", StringComparison.Ordinal), ["reused"]));
        (await State(host)).ShouldBe(before);
        (await host.Send("POST", "/v3/workspaces/A/sessions/s2/messages/list", "{}", AuthHost.Key("workspace"), 200))
            .GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task SameKey_InDifferentWorkspace_IsAnIndependentNamespace()
    {
        using var host = new AuthHost();
        await host.Seed();
        await host.Send("POST", "/v3/workspaces/B/sessions", """{"id":"s1"}""", AuthHost.Key("other"), 200);
        var first = await Send(host, Messages, Body, ["namespace"]);
        var second = await Send(host, Messages.Replace("/A/", "/B/", StringComparison.Ordinal), Body, ["namespace"], AuthHost.Key("other"));
        first.Status.ShouldBe(201);
        second.Status.ShouldBe(201);
        second.Bytes.ShouldNotBe(first.Bytes);
        (await host.Store.Idempotency.TryGetAsync("A", "namespace", default)).ShouldNotBeNull();
        (await host.Store.Idempotency.TryGetAsync("B", "namespace", default)).ShouldNotBeNull();
        await Count(host, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Expiry_At24HoursAndAfter_AllowsFreshAtomicOperation(int ticksAfter)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var host = new AuthHost { ServicesOverride = services => services.AddSingleton<TimeProvider>(clock) };
        await host.Seed();
        var start = clock.GetUtcNow();
        var first = await Send(host, Messages, Body, ["ttl"]);
        first.Status.ShouldBe(201);
        var record = (await host.Store.Idempotency.TryGetAsync("A", "ttl", default)).ShouldNotBeNull();
        record.ExpiresAt.ShouldBe(start.AddHours(24));
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        (await Send(host, Messages + "/", Body, ["ttl"])).Bytes.ShouldBe(first.Bytes);
        var different = Body.Replace("1e2", "101", StringComparison.Ordinal);
        Reused(await Send(host, Messages, different, ["ttl"]));
        await Count(host, 1);
        clock.Advance(TimeSpan.FromTicks(1 + ticksAfter));
        (await host.Store.Idempotency.TryGetAsync("A", "ttl", default)).ShouldBeNull();
        var fresh = await Send(host, Messages + "/", different, ["ttl"]);
        fresh.Status.ShouldBe(201, Encoding.UTF8.GetString(fresh.Bytes));
        fresh.Bytes.ShouldNotBe(first.Bytes);
        var replacement = (await host.Store.Idempotency.TryGetAsync("A", "ttl", default)).ShouldNotBeNull();
        replacement.RequestHash.ShouldNotBe(record.RequestHash);
        replacement.ExpiresAt.ShouldBe(clock.GetUtcNow().AddHours(24));
        Encoding.UTF8.GetBytes(replacement.ResponseBody).ShouldBe(fresh.Bytes);
        (await Send(host, Messages, different, ["ttl"])).Bytes.ShouldBe(fresh.Bytes);
        await Count(host, 2);
    }

    [Fact]
    public async Task ConcurrentSameKey_24RoundsOfEight_WithParallelKeyLoad()
    {
        using var host = new AuthHost();
        await host.Seed();
        var token = AuthHost.Key("workspace");
        for (var batch = 0; batch < 6; batch++)
        {
            await Task.WhenAll(Enumerable.Range(batch * 4, 4).Select(async round =>
            {
                var key = "concurrent-" + round;
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = Enumerable.Range(0, 8).Select(async index =>
                {
                    await start.Task;
                    return await Send(host, Messages + (index % 2 == 0 ? "/" : ""), Body, [key], token);
                }).ToArray();
                start.SetResult();
                var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30));
                foreach (var result in results)
                {
                    result.Status.ShouldBe(201, Encoding.UTF8.GetString(result.Bytes));
                    result.Bytes.ShouldBe(results[0].Bytes);
                }
                var record = (await host.Store.Idempotency.TryGetAsync("A", key, default)).ShouldNotBeNull();
                Encoding.UTF8.GetBytes(record.ResponseBody).ShouldBe(results[0].Bytes);
            }));
            await Count(host, (batch + 1) * 4);
        }
        output.WriteLine("24 rounds x 8 requests; four keys in parallel per batch; exactly 24 inserted messages.");
    }

    public static TheoryData<string, string, bool> DeniedHeaders()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var suffix in new[] { "", "/" })
        foreach (var identity in new[] { "none", "other", "peer", "revoked" })
        foreach (var multiple in new[] { false, true }) data.Add(suffix, identity, multiple);
        return data;
    }

    [Theory]
    [MemberData(nameof(DeniedHeaders))]
    public async Task DeniedCaller_CannotReadBodyOrReplay_BeforeHeaderValidation(string suffix, string identity, bool multiple)
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra, ServicesOverride = CountReplayReads };
        await host.Seed();
        var store = host.Store.ShouldBeOfType<CountingStore>();
        var grant = new GrantRecord("object-id", "A", GrantRoles.Workspace);
        await store.Grants.AddAsync(grant, default);
        var token = entra.Token();
        var first = await Send(host, Messages, Body, ["private-captured"], token);
        first.Status.ShouldBe(201);
        var record = (await store.Inner.Idempotency.TryGetAsync("A", "private-captured", default)).ShouldNotBeNull();
        if (identity == "revoked")
        {
            await store.Grants.RemoveAsync(grant, default);
            (await store.Grants.GetWorkspaceGrantsAsync("object-id", default)).Workspaces.ShouldBeEmpty();
        }
        else if (identity != "none") token = AuthHost.Key(identity);
        var before = await State(host);
        var reads = store.Reads;
        await using var unreadable = new UnreadableBody();
        var context = await host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = Messages + suffix;
            context.Request.ContentType = "application/json";
            context.Request.Headers["Idempotency-Key"] = multiple
                ? new StringValues(["private-captured", "second"])
                : new string('a', 256);
            if (identity != "none") context.Request.Headers.Authorization = "Bearer " + token;
            context.Request.Body = unreadable;
        });
        context.Response.StatusCode.ShouldBe(401);
        unreadable.Reads.ShouldBe(0);
        store.Reads.ShouldBe(reads);
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("status").GetInt32().ShouldBe(401);
        json.RootElement.GetProperty("detail").ValueKind.ShouldBe(JsonValueKind.String);
        json.RootElement.GetRawText().ShouldNotContain("private-captured");
        (await State(host)).ShouldBe(before);
        (await store.Inner.Idempotency.TryGetAsync("A", "private-captured", default)).ShouldBe(record);
        (await Send(host, Messages, Body, ["private-captured"])).Bytes.ShouldBe(first.Bytes);
    }

    [Theory]
    [InlineData("{", true, "json_invalid")]
    [InlineData("{", false, "json_invalid")]
    [InlineData("42", true, "value_error")]
    [InlineData("42", false, null)]
    public async Task BodyRead_PrecedesHeaderValidation_ThenCoreSchemaValidation(string body, bool multiple, string? errorType)
    {
        using var host = new AuthHost();
        await host.Seed();
        var response = await Boundary(host, body, multiple ? ["a", "b"] : [""]);
        response.Status.ShouldBe(422);
        if (errorType == "json_invalid") Validation(response, errorType, "body");
        else if (errorType == "value_error") Validation(response, errorType, "header", "idempotency-key");
        else
        {
            using var json = JsonDocument.Parse(response.Bytes);
            json.RootElement.GetProperty("detail").ValueKind.ShouldBe(JsonValueKind.String);
        }
        await Count(host, 0);
    }

    [Fact]
    public async Task Kestrel_LoopbackSeparatesAcceptedControl_InvalidFieldSyntax_AndValidRepeatedHeaders()
    {
        await using var host = new AuthHost();
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await host.Seed();
        var address = host.Http.BaseAddress.ShouldNotBeNull();
        IPAddress.IsLoopback(IPAddress.Parse(address.Host)).ShouldBeTrue();
        foreach (var (header, status) in new[]
        {
            ("Idempotency-Key: a\u0001b\r\n", 201),
            ("Idempotency-Key: a\rb\r\n", 400),
            ("Idempotency-Key: a\nb\r\n", 400),
            ("Idempotency-Key: a\r\nb\r\n", 400),
            ("Idempotency-Key: private-one\r\nIdempotency-Key: private-two\r\n", 422),
            ("Idempotency-Key: duplicate\r\nIdempotency-Key: duplicate\r\n", 422),
            ("Idempotency-Key: comma,one\r\n", 201),
            ("Idempotency-Key:\r\n", 422),
        })
        {
            // No body on malformed-header probes: trailing unread data can reset TCP after Kestrel rejects the header.
            var response = await RawKestrel(address.Port, header, status == 400 ? "" : Body);
            output.WriteLine($"{JsonSerializer.Serialize(header)} -> {response.Split("\r\n", StringSplitOptions.None)[0]}");
            response.ShouldStartWith("HTTP/1.1 " + status + " ");
            if (header.Contains("private-one", StringComparison.Ordinal) || header.Contains("duplicate", StringComparison.Ordinal))
            {
                response.ShouldContain("[\"header\",\"idempotency-key\"]");
                response.ShouldContain("value_error");
                response.ShouldNotContain("private-one");
                response.ShouldNotContain("private-two");
                response.ShouldNotContain("duplicate");
            }
        }
        (await host.Store.Idempotency.TryGetAsync("A", "a\u0001b", default)).ShouldNotBeNull();
        (await host.Store.Idempotency.TryGetAsync("A", "comma,one", default)).ShouldNotBeNull();
        await Count(host, 2);
    }

    [Fact]
    public void HttpClientCrLfValidation_IsLocalAndNotTransportEvidence()
    {
        using var request = new HttpRequestMessage();
        Should.Throw<FormatException>(() => request.Headers.Add("Idempotency-Key", "a\r\nb"));
    }

    private static async Task<string> RawKestrel(int port, string header, string requestBody)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await using var stream = tcp.GetStream();
        var body = Encoding.UTF8.GetBytes(requestBody);
        var request = Encoding.ASCII.GetBytes($"POST {Messages}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
            $"Authorization: Bearer {AuthHost.Key("workspace")}\r\nContent-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n{header}\r\n");
        await stream.WriteAsync(request, timeout.Token);
        await stream.WriteAsync(body, timeout.Token);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, timeout.Token);
        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static async Task<(int Status, byte[] Bytes)> Send(AuthHost host, string path, string body, string[] keys, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", token ?? AuthHost.Key("workspace"));
        if (keys.Length != 0) request.Headers.TryAddWithoutValidation("Idempotency-Key", keys).ShouldBeTrue();
        using var response = await host.Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<(int Status, byte[] Bytes)> Boundary(AuthHost host, string body, string[] keys)
    {
        // HttpClient/TestServer can drop empty values; explicitly supply application values here, with separate raw-wire proof.
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var context = await host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = Messages;
            context.Request.ContentType = "application/json";
            context.Request.Headers.Authorization = "Bearer " + AuthHost.Key("workspace");
            context.Request.Headers["Idempotency-Key"] = new StringValues(keys);
            context.Request.Body = input;
        });
        using var result = new MemoryStream();
        await context.Response.Body.CopyToAsync(result);
        return (context.Response.StatusCode, result.ToArray());
    }

    private static void Validation((int Status, byte[] Bytes) response, string type, params string[] location)
    {
        response.Status.ShouldBe(422, Encoding.UTF8.GetString(response.Bytes));
        using var json = JsonDocument.Parse(response.Bytes);
        json.RootElement.GetProperty("status").GetInt32().ShouldBe(422);
        var detail = json.RootElement.GetProperty("detail");
        detail.GetArrayLength().ShouldBe(1);
        detail[0].GetProperty("loc").EnumerateArray().Select(value => value.GetString()).ShouldBe(location);
        detail[0].GetProperty("type").GetString().ShouldBe(type);
    }

    private static void Reused((int Status, byte[] Bytes) response)
    {
        response.Status.ShouldBe(422, Encoding.UTF8.GetString(response.Bytes));
        using var json = JsonDocument.Parse(response.Bytes);
        json.RootElement.GetProperty("type").GetString().ShouldBe(ProblemTypes.IdempotencyKeyReused);
        json.RootElement.GetProperty("detail").ValueKind.ShouldBe(JsonValueKind.String);
    }

    private static async Task Count(AuthHost host, int expected) =>
        (await host.Send("POST", Messages + "/list", "{}", AuthHost.Key("workspace"), 200))
            .GetProperty("total").GetInt32().ShouldBe(expected);

    private static async Task<string[]> State(AuthHost host)
    {
        List<string> state = [];
        foreach (var path in new[] { "/v3/workspaces/A/peers/list", "/v3/workspaces/A/sessions/list", Messages + "/list" })
            state.Add((await host.Send("POST", path, "{}", AuthHost.Key("workspace"), 200)).GetRawText());
        state.Add((await host.Send("GET", "/v3/workspaces/A/sessions/s1/peers", null, AuthHost.Key("workspace"), 200)).GetRawText());
        return state.ToArray();
    }

    private static void CountReplayReads(IServiceCollection services)
    {
        var descriptor = services.Single(service => service.ServiceType == typeof(IMemoryStore));
        var create = descriptor.ImplementationFactory.ShouldNotBeNull();
        services.Remove(descriptor);
        services.AddSingleton<IMemoryStore>(provider => new CountingStore((IMemoryStore)create(provider)));
    }

    private sealed class CountingStore(IMemoryStore inner) : IMemoryStore, IIdempotencyStore
    {
        private long _reads;
        public long Reads => Interlocked.Read(ref _reads);
        public IMemoryStore Inner => inner;
        public IWorkspaceStore Workspaces => inner.Workspaces;
        public IPeerStore Peers => inner.Peers;
        public ISessionStore Sessions => inner.Sessions;
        public IMessageStore Messages => inner.Messages;
        public IGrantStore Grants => inner.Grants;
        public IIdempotencyStore Idempotency => this;
        public Task<IdempotencyRecord?> TryGetAsync(string workspaceName, string key, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            return inner.Idempotency.TryGetAsync(workspaceName, key, ct);
        }
    }

    private sealed class UnreadableBody : Stream
    {
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            throw new InvalidOperationException("A denied request must not read the body.");
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            throw new InvalidOperationException("A denied request must not read the body.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
