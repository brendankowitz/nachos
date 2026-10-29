using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Deriver;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using Nachos.Hosting;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Core.Tests.Deriver;

/// <summary>An in-memory host with the Deriver registered, a scripted chat client and an offline embedding generator.</summary>
internal sealed class DeriverFixture : IDisposable
{
    public const string WorkspaceId = "W";
    public const string SessionId = "S";

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly ServiceProvider _provider;

    public DeriverFixture(Action<NachosBuilder>? configureAi = null, Action<NachosOptions>? configureOptions = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Profiles:default:Model"] = "scripted" })
            .Build());
        services.AddNachos(builder =>
        {
            builder.UseInMemory();
            if (configureAi is null)
            {
                builder.UseChatClient((_, _) => Chat).UseEmbeddingGenerator(_ => Embeddings);
            }
            else
            {
                configureAi(builder);
            }

            builder.AddDeriver();
        });
        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Store = _provider.GetRequiredService<IMemoryStore>();
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));

    public ScriptedChatClient Chat { get; } = new();

    public FlakyEmbeddingGenerator Embeddings { get; } = new();

    public IMemoryStore Store { get; }

    public IServiceProvider Services => _provider;

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Runs the registered representation handler in a scope of its own, as a worker would.</summary>
    public async Task HandleAsync(WorkLease lease)
    {
        using var scope = _provider.CreateScope();
        await Handler(scope.ServiceProvider).HandleAsync(lease, Ct);
    }

    public DeriverService Handler() => Handler(_provider.CreateScope().ServiceProvider);

    private static DeriverService Handler(IServiceProvider services) =>
        services.GetServices<IWorkHandler>().Single(h => h.TaskType == WorkTaskType.Representation).ShouldBeOfType<DeriverService>();

    /// <summary>Workspace W and session S, where bob observes others and alice does not.</summary>
    public async Task SeedAsync()
    {
        await Store.Workspaces.GetOrCreateAsync(WorkspaceId, null, null, Ct);
        await Store.Sessions.GetOrCreateAsync(
            WorkspaceId,
            SessionId,
            null,
            null,
            new Dictionary<string, SessionPeerConfig> { ["alice"] = new(), ["bob"] = new(ObserveOthers: true) },
            Ct);
    }

    /// <summary>Appends one message; only alice's messages enqueue derivation, so every lease is alice's unit.</summary>
    public async Task<MessageRecord> SayAsync(string peer, string content)
    {
        var intent = peer == "alice" ? DeriveIntent.Derive : DeriveIntent.Skip;
        var message = (await Store.Messages.AppendAsync(
            WorkspaceId, SessionId, [new NewMessage(peer, content, 5, null, Clock.GetUtcNow())], null, [intent], Ct)).Single();
        Clock.Advance(TimeSpan.FromMinutes(1));
        return message;
    }

    public async Task<WorkLease> AcquireAsync(string owner = "worker")
    {
        var policy = new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Representation] = new(0, TimeSpan.Zero, 1000) };
        return (await Store.WorkQueue.TryAcquireAsync(owner, policy, Lease, Ct)).ShouldNotBeNull();
    }

    public async Task<IReadOnlyList<ConclusionRecord>> ConclusionsAsync(string observer, string observed = "alice") =>
        (await Store.Conclusions.ListAsync(WorkspaceId, null, ConclusionOrder.CreatedAt, new(Size: 100), Ct)).Items
            .Where(c => c.ObserverName == observer && c.ObservedName == observed)
            .ToList();

    public async Task<QueueCounts> QueueAsync() =>
        (await Store.WorkQueue.GetStatusAsync(WorkspaceId, null, null, null, Ct)).Totals;

    public ScriptedChatClient Facts(params (string Content, string[] Sources)[] facts) =>
        Chat.EnqueueJson(new ExtractionResult([.. facts.Select(f => new ExtractedFact(f.Content, f.Sources))]));

    public void Dispose() => _provider.Dispose();
}

/// <summary>A <see cref="DeterministicEmbeddingGenerator"/> that can be told to fail its next call.</summary>
internal sealed class FlakyEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly DeterministicEmbeddingGenerator _inner = new();

    public bool FailNext { get; set; }

    public int Calls { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (FailNext)
        {
            FailNext = false;
            throw new HttpRequestException("embedding service unavailable");
        }

        return _inner.GenerateAsync(values, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
