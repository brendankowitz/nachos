using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

internal sealed class InMemoryEmbeddingStore(InMemoryState state) : IEmbeddingStore
{
    public Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(int limit, CancellationToken ct) =>
        state.Run<IReadOnlyList<PendingEmbedding>>(
            () =>
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
                var pending = new List<StoredConclusion>();
                foreach (var workspace in state.AllWorkspaces())
                {
                    lock (workspace.Gate)
                    {
                        pending.AddRange(workspace.Conclusions.Values.Where(c => c.IsLive && c.State == EmbeddingState.Pending));
                    }
                }

                return
                [
                    .. pending
                        .OrderBy(c => c.Record.CreatedAt)
                        .ThenBy(c => c.Order)
                        .Take(limit)
                        .Select(c => new PendingEmbedding(c.Record.WorkspaceName, c.Record.PublicId, c.Record.Content)),
                ];
            },
            ct);

    public Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(pending);
                var owned = VectorMath.Own(embedding, nameof(embedding));
                Update(pending, stored => stored with { Embedding = owned, State = EmbeddingState.Synced });
            },
            ct);

    public Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(pending);
                Update(pending, stored => stored with { State = EmbeddingState.Failed });
            },
            ct);

    /// <summary>Applies <paramref name="change"/> only while the conclusion is still pending with the claimed content.</summary>
    private void Update(PendingEmbedding pending, Func<StoredConclusion, StoredConclusion> change)
    {
        if (state.FindWorkspace(pending.WorkspaceName) is not { } workspace)
        {
            return;
        }

        lock (workspace.Gate)
        {
            if (workspace.Conclusions.TryGetValue(pending.ConclusionId, out var stored) && stored.IsPendingWith(pending.Content))
            {
                workspace.Conclusions[pending.ConclusionId] = change(stored);
            }
        }
    }
}
