using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// The store lifecycle and helpers shared by the M3 contract suites (<see cref="M3StoreContractTests"/>,
/// <see cref="SearchContractTests"/>, <see cref="DeletionContractTests"/>, <see cref="DeletionPurgeContractTests"/>).
/// </summary>
/// <remarks>
/// The suites claim work across workspaces, so each test must start from an empty queue: a provider whose stores share
/// one database overrides <see cref="ResetQueueAsync"/>, and every derived class joins the non-parallel
/// <see cref="M2StoreContracts"/>. They are separate from <see cref="StoreContractTests"/> so a provider can adopt them
/// one milestone task at a time.
/// </remarks>
public abstract class M3ContractTestBase : IAsyncLifetime
{
    protected static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    protected static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly ConcurrentBag<IMemoryStore> _stores = [];

    protected static CancellationToken Ct => CancellationToken.None;

    /// <summary>Creates a store whose time-based values come from <paramref name="clock"/>.</summary>
    protected abstract IMemoryStore CreateStore(TimeProvider clock);

    /// <summary>Empties the work queue of a store that tests share. A no-op by default, for a provider whose stores start empty.</summary>
    protected virtual Task ResetQueueAsync(IMemoryStore store) => Task.CompletedTask;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    protected async Task<(IMemoryStore Store, FakeTimeProvider Clock)> NewStoreAsync()
    {
        var clock = new FakeTimeProvider(Start);
        var store = CreateStore(clock);
        _stores.Add(store);
        await ResetQueueAsync(store);
        return (store, clock);
    }

    protected static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    protected static NewMessage Msg(string peer, string content, DateTimeOffset? createdAt = null) =>
        new(peer, content, Math.Max(1, content.Length / 4), null, createdAt);

    protected static async Task<string> NewWorkspaceAsync(IMemoryStore store)
    {
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        return workspace;
    }

    protected static async Task<(string Workspace, string Session)> NewSessionAsync(IMemoryStore store)
    {
        var workspace = await NewWorkspaceAsync(store);
        var session = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        return (workspace, session);
    }

    protected static Dictionary<string, SessionPeerConfig> Members(params string[] names) =>
        names.ToDictionary(name => name, _ => new SessionPeerConfig());

    /// <summary>A unit vector along <paramref name="axis"/>, optionally tilted towards <paramref name="towards"/>.</summary>
    protected static float[] Vector(int axis, int? towards = null, float tilt = 0)
    {
        var vector = new float[VectorSpace.Dimensions];
        vector[axis] = 1;
        if (towards is { } other)
        {
            vector[other] = tilt;
        }

        return vector;
    }

    /// <summary>Leases the next due item of <paramref name="type"/>, or null.</summary>
    protected static Task<WorkLease?> AcquireAsync(IMemoryStore store, WorkTaskType type, string owner = "worker-1") =>
        store.WorkQueue.TryAcquireAsync(
            owner, new Dictionary<WorkTaskType, WorkBatchPolicy> { [type] = WorkBatchPolicy.Immediate }, Lease, Ct);

    /// <summary>Embeds every pending chunk of <paramref name="vectors"/>' keys (message id, chunk index).</summary>
    protected static async Task EmbedChunksAsync(IMemoryStore store, IReadOnlyDictionary<(string MessageId, int ChunkIndex), float[]> vectors)
    {
        var pending = await store.Embeddings.ListPendingAsync(100_000, Ct);
        foreach (var item in pending.Where(p => p.Kind == PendingEmbeddingKind.MessageChunk))
        {
            if (vectors.TryGetValue((item.SourceId, item.ChunkIndex), out var vector))
            {
                await store.Embeddings.SaveAsync(item, vector, "test-model", Ct);
            }
        }
    }

    protected static async Task<ConflictException> ShouldConflictAsync(Func<Task> write, string detail)
    {
        var conflict = await Should.ThrowAsync<ConflictException>(write);
        conflict.Message.ShouldBe(detail);
        return conflict;
    }
}
