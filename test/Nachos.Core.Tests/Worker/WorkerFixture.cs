using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Worker;
using Nachos.DataLayer.InMemory;
using Nachos.Hosting;
using Shouldly;

namespace Nachos.Core.Tests.Worker;

/// <summary>
/// An in-memory store behind an <see cref="ObservedStore"/>, a fake clock, and a container to build workers from.
/// </summary>
internal sealed class WorkerFixture : IAsyncDisposable
{
    public const string WorkspaceId = "W";
    public const string SessionId = "S";

    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly ServiceProvider _provider;
    private readonly List<Microsoft.Extensions.Hosting.BackgroundService> _started = [];

    /// <param name="handlerType">The task type of the <see cref="Handler"/> registered; null registers none.</param>
    public WorkerFixture(
        WorkTaskType? handlerType = WorkTaskType.Representation,
        Action<NachosBuilder>? configure = null,
        Action<NachosOptions>? configureNachos = null,
        Action<WorkerOptions>? configureWorker = null)
    {
        Inner = new InMemoryMemoryStore(Clock);
        Store = new ObservedStore(Inner);
        var services = new ServiceCollection();
        services.AddSingleton(Logs);
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Profiles:default:Model"] = "fake" })
            .Build());
        services.AddNachos(builder =>
        {
            builder.UseInMemory();
            configure?.Invoke(builder);
        });
        services.Replace(ServiceDescriptor.Singleton<IMemoryStore>(Store));
        if (handlerType is { } type)
        {
            Handler = new TestHandler(type, Store, Clock);
            services.AddSingleton<IWorkHandler>(Handler);
        }

        services.TryAddSingleton<WorkerMetrics>();
        services.Configure<NachosOptions>(options =>
        {
            options.Deriver.LeaseDuration = Lease;
            options.Deriver.MaxAttempts = 3;
            configureNachos?.Invoke(options);
        });
        services.Configure<WorkerOptions>(options =>
        {
            options.MaxConcurrency = 1;
            options.IdleDelay = TimeSpan.FromSeconds(1);
            options.MaxIdleDelay = TimeSpan.FromSeconds(1);
            configureWorker?.Invoke(options);
        });
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));

    public InMemoryMemoryStore Inner { get; }

    /// <summary>What every <see cref="ILogger{TCategoryName}"/> from <see cref="Services"/> logged.</summary>
    public RecordingLogs Logs { get; } = new();

    public ObservedStore Store { get; }

    /// <summary>The registered test handler, if any.</summary>
    public TestHandler? Handler { get; }

    public IServiceProvider Services => _provider;

    public WorkerMetrics Metrics => _provider.GetRequiredService<WorkerMetrics>();

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>A started consumer; stopped when the fixture is disposed, unless the test stops it.</summary>
    public async Task<QueueConsumerService> StartConsumerAsync()
    {
        var consumer = ActivatorUtilities.CreateInstance<QueueConsumerService>(_provider);
        _started.Add(consumer);
        await consumer.StartAsync(Ct);
        return consumer;
    }

    public async Task SeedAsync(params string[] sessions)
    {
        await Store.Workspaces.GetOrCreateAsync(WorkspaceId, null, null, Ct);
        foreach (var session in sessions.DefaultIfEmpty(SessionId))
        {
            await Store.Sessions.GetOrCreateAsync(
                WorkspaceId,
                session,
                null,
                null,
                new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveOthers: true), ["bob"] = new(ObserveOthers: true) },
                Ct);
        }
    }

    /// <summary>Appends one message that enqueues derivation.</summary>
    public async Task<MessageRecord> SayAsync(string content, string peer = "alice", string session = SessionId) =>
        (await Store.Messages.AppendAsync(
            WorkspaceId, session, [new NewMessage(peer, content, 5, null, null)], null, [DeriveIntent.Derive], Ct)).Single();

    public async Task<QueueCounts> QueueAsync() =>
        (await Store.WorkQueue.GetStatusAsync(WorkspaceId, null, null, null, Ct)).Totals;

    /// <summary>Takes the next due unit as another worker would.</summary>
    public Task<WorkLease?> CompeteAsync(string owner = "competitor") =>
        Inner.WorkQueue.TryAcquireAsync(
            owner, new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Representation] = WorkBatchPolicy.Immediate }, Lease, Ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _started)
        {
            await service.StopAsync(Ct);
            service.Dispose();
        }

        await _provider.DisposeAsync();
    }
}

/// <summary>Moves the fake clock in steps until a condition holds, letting the workers run between steps.</summary>
internal static class Pump
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task UntilAsync(FakeTimeProvider clock, Func<Task<bool>> done, TimeSpan step)
    {
        var watch = Stopwatch.StartNew();
        while (!await done())
        {
            watch.Elapsed.ShouldBeLessThan(Timeout, "the condition was not reached");
            clock.Advance(step);
            // A real yield, so the workers' continuations run; the outcome never depends on how long it takes.
            await Task.Delay(1);
        }
    }

    public static Task UntilAsync(FakeTimeProvider clock, Func<bool> done, TimeSpan step) =>
        UntilAsync(clock, () => Task.FromResult(done()), step);
}

/// <summary>A singleton handler for one task type that records its calls and completes each lease unless told otherwise.</summary>
internal sealed class TestHandler(WorkTaskType taskType, IMemoryStore store, TimeProvider clock) : IWorkHandler
{
    public WorkTaskType TaskType => taskType;

    public WorkBatchPolicy BatchPolicy => WorkBatchPolicy.Immediate;

    /// <summary>Replaces the default behaviour (complete the lease).</summary>
    public Func<WorkLease, CancellationToken, Task>? Behavior { get; set; }

    public ConcurrentQueue<(WorkLease Lease, DateTimeOffset At)> Calls { get; } = new();

    public async Task HandleAsync(WorkLease lease, CancellationToken cancellationToken)
    {
        Calls.Enqueue((lease, clock.GetUtcNow()));
        if (Behavior is { } behavior)
        {
            await behavior(lease, cancellationToken);
            return;
        }

        await store.WorkQueue.CompleteAsync(lease, cancellationToken);
    }
}
