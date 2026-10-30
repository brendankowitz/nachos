using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Memory;
using Nachos.Core.Tests.Deriver;
using Nachos.DataLayer.InMemory;
using Shouldly;

namespace Nachos.Core.Tests.Memory;

/// <summary>
/// An in-memory host with the memory services and an offline embedding generator. Workspace W has peers alice and bob
/// and sessions S1 and S2; bob observes others in both, so alice's messages enqueue derivation work.
/// </summary>
internal sealed class MemoryFixture : IDisposable
{
    public const string W = "W";

    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public MemoryFixture()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings);
        services.AddNachos(builder => builder.UseInMemory());
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _provider.CreateScope();
        Store = _scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        Store.ShouldBeOfType<InMemoryMemoryStore>();
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));

    public FlakyEmbeddingGenerator Embeddings { get; } = new();

    public IMemoryStore Store { get; }

    public NachosService Service => Get<NachosService>();

    public ConclusionService Conclusions => Get<ConclusionService>();

    public RepresentationBuilder Representations => Get<RepresentationBuilder>();

    public PeerContextService PeerContext => Get<PeerContextService>();

    private static CancellationToken Ct => CancellationToken.None;

    public async Task<MemoryFixture> SeedAsync()
    {
        await Store.Workspaces.GetOrCreateAsync(W, null, null, Ct);
        foreach (var session in new[] { "S1", "S2" })
        {
            await Store.Sessions.GetOrCreateAsync(W, session, null, null,
                new Dictionary<string, SessionPeerConfig> { ["alice"] = new(), ["bob"] = new(ObserveOthers: true) }, Ct);
        }

        return this;
    }

    /// <summary>Stores one embedded conclusion, a minute after the previous one.</summary>
    public async Task<ConclusionRecord> AddAsync(
        string content,
        string? session = "S1",
        string observer = "bob",
        string observed = "alice",
        ConclusionLevel level = ConclusionLevel.Explicit)
    {
        var vector = await Embeddings.GenerateVectorAsync(content);
        var created = await Store.Conclusions.InsertAsync(W,
            [new NewConclusion(observer, observed, session, level, content, vector, [], null)], Ct);
        Clock.Advance(TimeSpan.FromMinutes(1));
        return created.Single();
    }

    /// <summary>Re-derives <paramref name="conclusion"/> <paramref name="times"/> more times, as the Deriver would.</summary>
    public async Task CorroborateAsync(ConclusionRecord conclusion, int times = 1)
    {
        var policy = new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Representation] = WorkBatchPolicy.Immediate };
        for (var i = 0; i < times; i++)
        {
            await SayAsync("alice", $"corroboration {conclusion.PublicId} {i}");
            var lease = (await Store.WorkQueue.TryAcquireAsync("test", policy, TimeSpan.FromMinutes(5), Ct)).ShouldNotBeNull();
            await Store.Conclusions.CommitDerivationAsync(lease, new(W, [], [conclusion.PublicId], []), Ct);
        }
    }

    /// <summary>Appends one message to <paramref name="session"/>, enqueueing derivation when the sender is observed.</summary>
    public async Task SayAsync(string peer, string content, string session = "S1") =>
        await Store.Messages.AppendAsync(W, session, [new NewMessage(peer, content, 5, null, Clock.GetUtcNow())], null,
            [DeriveIntent.Derive], Ct);

    private T Get<T>()
        where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
