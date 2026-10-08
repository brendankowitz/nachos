using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Stores;
using Nachos.Testing;
using NSubstitute;
using NSubstitute.Extensions;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class TransportBoundaryTests : ApiTest
{
    [Theory]
    [InlineData("conflict", 409, "Conflict")]
    [InlineData("auth", 401, "Unauthorized")]
    [InlineData("domain", 422, "Unprocessable Entity")]
    [InlineData("corrupt", 500, "Internal Server Error")]
    [InlineData("duplicate-race", 500, "Internal Server Error")]
    [InlineData("stored-json", 500, "Internal Server Error")]
    [InlineData("unrelated-cancellation", 500, "Internal Server Error")]
    public async Task ExceptionMapping_PreservesExpectedStatusAndHidesServerDetails(string kind, int status, string title)
    {
        Exception failure = kind switch
        {
            "conflict" => new ConflictException("safe detail"),
            "auth" => new AuthException("safe detail"),
            "domain" => new NachosValidationException("safe detail"),
            "duplicate-race" => new IdempotencyDuplicateException("private-key"),
            "stored-json" => new JsonException("private stored JSON"),
            "unrelated-cancellation" => new OperationCanceledException("private cancellation"),
            _ => new InvalidOperationException("private corrupt configuration"),
        };
        // The substitute isolates transport error classification, not Core/provider correctness.
        var service = Substitute.For<INachosClient>();
        service.ReturnsForAll<Task<Workspace>>(_ => Task.FromException<Workspace>(failure));
        var logs = new ErrorLog();
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(service);
            services.AddLogging(logging => logging.AddProvider(logs));
        }));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v3/workspaces",
            new StringContent("""{"id":"w"}""", Encoding.UTF8, "application/json"));
        ((int)response.StatusCode).ShouldBe(status);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Problem(json.RootElement, status, JsonValueKind.String);
        json.RootElement.GetProperty("title").GetString().ShouldBe(title);
        if (status == 500)
        {
            json.RootElement.GetProperty("detail").GetString().ShouldBe("An unexpected error occurred.");
            logs.Exceptions.ShouldContain(failure);
        }
        else
        {
            json.RootElement.GetProperty("detail").GetString().ShouldBe("safe detail");
        }
    }

    [Fact]
    public async Task CapturedResponse_StoredStatusAndBytesAreWrittenWithoutReserialization()
    {
        await SeedSession();
        var real = Factory.Services.GetRequiredService<IMemoryStore>();
        var replay = Substitute.For<IIdempotencyStore>();
        const string captured = " [ { \"sentinel\" : 1e2, \"nullable\":null } ] \n";
        const string body = """{"messages":[{"content":"a","peer_id":"p"}]}""";
        using var document = JsonDocument.Parse(body);
        var hash = Nachos.Core.Idempotency.RequestHasher.Hash("POST", "/v3/workspaces/{w}/sessions/{s}/messages",
            new Dictionary<string, string> { ["w"] = "w", ["s"] = "s" },
            Nachos.Core.Idempotency.CanonicalJson.Serialize(document.RootElement));
        replay.TryGetAsync("w", "captured", Arg.Any<CancellationToken>()).Returns(
            new Nachos.Abstractions.Domain.IdempotencyRecord("captured", hash, 202, captured, DateTimeOffset.MaxValue));
        var store = Substitute.For<IMemoryStore>();
        store.Sessions.Returns(real.Sessions);
        store.Idempotency.Returns(replay);
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(store)));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, M)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", "captured");
        using var response = await client.SendAsync(request);
        ((int)response.StatusCode).ShouldBe(202);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Encoding.UTF8.GetBytes(captured));
    }

    [Theory]
    [InlineData("/health", 503)]
    [InlineData("/health/ready", 503)]
    [InlineData("/health/live", 200)]
    public async Task Readiness_ObservesStoreFailureWithoutBreakingLiveness(string path, int expected)
    {
        var workspaces = Substitute.For<IWorkspaceStore>();
        workspaces.ListAsync(null, Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Page<Nachos.Abstractions.Domain.WorkspaceRecord>>(new IOException("store unavailable")));
        var store = Substitute.For<IMemoryStore>();
        store.Workspaces.Returns(workspaces);
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(store)));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        ((int)response.StatusCode).ShouldBe(expected);
    }

    [Theory]
    [InlineData("POST", "/v3/workspaces", """{"id":"w"}""")]
    [InlineData("POST", "/v3/workspaces/list", "{}")]
    [InlineData("PUT", W, "{}")]
    [InlineData("POST", W + "/peers", """{"id":"p"}""")]
    [InlineData("POST", W + "/peers/list", "{}")]
    [InlineData("PUT", W + "/peers/p", "{}")]
    [InlineData("POST", W + "/peers/p/sessions", "{}")]
    [InlineData("POST", W + "/sessions", """{"id":"s"}""")]
    [InlineData("POST", W + "/sessions/list", "{}")]
    [InlineData("PUT", S, "{}")]
    [InlineData("POST", S + "/peers", """{"p":{}}""")]
    [InlineData("PUT", S + "/peers", """{"p":{}}""")]
    [InlineData("DELETE", S + "/peers", """["p"]""")]
    [InlineData("GET", S + "/peers", null)]
    [InlineData("GET", S + "/peers/p/config", null)]
    [InlineData("PUT", S + "/peers/p/config", "{}")]
    [InlineData("POST", M + "/list", "{}")]
    [InlineData("GET", M + "/m", null)]
    [InlineData("PUT", M + "/m", "{}")]
    public async Task RequestAbort_ReachesEveryTypedOperation(string method, string path, string? body)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<T> Wait<T>(CancellationToken token)
        {
            entered.TrySetResult(token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return default!;
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }
        var service = Substitute.For<INachosClient>();
        service.ReturnsForAll<Task<Workspace>>(call => Wait<Workspace>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task<Peer>>(call => Wait<Peer>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task<Session>>(call => Wait<Session>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task<Message>>(call => Wait<Message>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task<SessionPeerConfig>>(call => Wait<SessionPeerConfig>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task<Page<Workspace>>>(call => Wait<Page<Workspace>>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task<Page<Peer>>>(call => Wait<Page<Peer>>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task<Page<Session>>>(call => Wait<Page<Session>>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task<Page<Message>>>(call => Wait<Page<Message>>(call.Arg<CancellationToken>()));
        service.ReturnsForAll<Task>(call => Wait<object>(call.Arg<CancellationToken>()));
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(service)));
        using var client = factory.CreateClient();
        using var abort = new CancellationTokenSource();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        var response = client.SendAsync(request, abort.Token);
        var downstream = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        downstream.CanBeCanceled.ShouldBeTrue();
        await abort.CancelAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Should.ThrowAsync<OperationCanceledException>(async () => await response);
    }

    [Fact]
    public async Task RequestAbort_CancelsBodyParsing()
    {
        _ = Client;
        await using var body = new WaitingBody();
        using var abort = new CancellationTokenSource();
        var response = Factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/v3/workspaces";
            context.Request.ContentType = "application/json";
            context.Request.Body = body;
        }, abort.Token);
        (await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10))).CanBeCanceled.ShouldBeTrue();
        await abort.CancelAsync();
        await body.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await response).Response.StatusCode.ShouldBe(499);
    }

    [Fact]
    public async Task RequestAbort_ReachesRawMessageCoreStoreOperation()
    {
        await SeedSession();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Nachos.Abstractions.Domain.IdempotencyRecord?> Block(CancellationToken ct)
        {
            entered.SetResult(ct);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return null;
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }
        var replay = Substitute.For<IIdempotencyStore>();
        replay.TryGetAsync("w", "blocked", Arg.Any<CancellationToken>())
            .Returns(call => Block(call.Arg<CancellationToken>()));
        var store = Substitute.For<IMemoryStore>();
        store.Sessions.Returns(Factory.Services.GetRequiredService<IMemoryStore>().Sessions);
        store.Idempotency.Returns(replay);
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(store)));
        using var client = factory.CreateClient();
        using var abort = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, M)
        {
            Content = new StringContent("""{"messages":[{"content":"a","peer_id":"p"}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", "blocked");
        var response = client.SendAsync(request, abort.Token);
        (await entered.Task.WaitAsync(TimeSpan.FromSeconds(10))).CanBeCanceled.ShouldBeTrue();
        await abort.CancelAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Should.ThrowAsync<OperationCanceledException>(async () => await response);
    }

    [Fact]
    public async Task DefaultAuth_IsClosedWithoutInventedCredentials()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v3/workspaces",
            new StringContent("""{"id":"w"}""", Encoding.UTF8, "application/json"));
        ((int)response.StatusCode).ShouldBe(401);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Problem(document.RootElement, 401, JsonValueKind.String);
    }

    [Fact]
    public void AuthDisabledOutsideDevelopment_FailsStartup()
    {
        using var root = new NachosApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        Should.Throw<InvalidOperationException>(() => factory.CreateClient())
            .Message.ShouldContain("only in Development");
    }

    private sealed class ErrorLog : ILoggerProvider, ILogger
    {
        public ConcurrentBag<Exception> Exceptions { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
        public void Dispose() { }
    }

    private sealed class WaitingBody : Stream
    {
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(cancellationToken);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
