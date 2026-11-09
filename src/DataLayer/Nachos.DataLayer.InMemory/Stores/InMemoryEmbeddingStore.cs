using System.Text.Json.Nodes;
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
                var pending = new List<(DateTimeOffset CreatedAt, long Order, PendingEmbedding Item)>();
                foreach (var workspace in state.AllWorkspaces())
                {
                    lock (workspace.Gate)
                    {
                        pending.AddRange(workspace.Conclusions.Values
                            .Where(c => c.IsLive && c.State == EmbeddingState.Pending)
                            .Select(c => (c.Record.CreatedAt, c.Order, PendingEmbedding.ForConclusion(workspace.Name, c.Record.PublicId, c.Record.Content))));
                        pending.AddRange(PendingChunkSessions(workspace)
                            .SelectMany(session => session.Chunks.Values)
                            .Where(chunk => chunk.State == EmbeddingState.Pending)
                            .Select(chunk => (chunk.CreatedAt, chunk.Order, PendingEmbedding.ForChunk(workspace.Name, chunk.MessageId, chunk.ChunkIndex, chunk.Content))));
                    }
                }

                return [.. pending.OrderBy(p => p.CreatedAt).ThenBy(p => p.Order).Take(limit).Select(p => p.Item)];
            },
            ct);

    public async Task<long> CountPendingAsync(CancellationToken ct) =>
        await CountPendingAsync(PendingEmbeddingKind.Conclusion, ct).ConfigureAwait(false)
        + await CountPendingAsync(PendingEmbeddingKind.MessageChunk, ct).ConfigureAwait(false);

    public Task<long> CountPendingAsync(PendingEmbeddingKind kind, CancellationToken ct) =>
        state.Run(
            () =>
            {
                long count = 0;
                foreach (var workspace in state.AllWorkspaces())
                {
                    lock (workspace.Gate)
                    {
                        count += kind == PendingEmbeddingKind.Conclusion
                            ? workspace.Conclusions.Values.Count(c => c.IsLive && c.State == EmbeddingState.Pending)
                            : PendingChunkSessions(workspace).Sum(session => session.Chunks.Values.Count(chunk => chunk.State == EmbeddingState.Pending));
                    }
                }

                return count;
            },
            ct);

    public Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, string embeddingModel, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(pending);
                ArgumentNullException.ThrowIfNull(embeddingModel);
                var owned = VectorMath.Own(embedding, nameof(embedding));
                Update(
                    pending,
                    stored => stored with
                    {
                        Record = stored.Record with { InternalMetadata = WithModel(stored.Record.InternalMetadata, embeddingModel) },
                        Embedding = owned,
                        State = EmbeddingState.Synced,
                    },
                    chunk => chunk with { Embedding = owned, State = EmbeddingState.Synced });
            },
            ct);

    public Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(pending);
                Update(pending, stored => stored with { State = EmbeddingState.Failed }, chunk => chunk with { State = EmbeddingState.Failed });
            },
            ct);

    public Task<IReadOnlyList<MessageRecord>> ListUnchunkedMessagesAsync(int limit, CancellationToken ct) =>
        state.Run<IReadOnlyList<MessageRecord>>(
            () =>
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
                var unchunked = new List<(MessageRecord Message, long Order)>();
                foreach (var workspace in state.AllWorkspaces())
                {
                    lock (workspace.Gate)
                    {
                        if (workspace.IsDeleting)
                        {
                            continue;
                        }

                        foreach (var session in workspace.Sessions.Values.Where(session => !session.IsDeleting))
                        {
                            var chunked = session.Chunks.Keys.Select(key => key.MessageId).ToHashSet(StringComparer.Ordinal);
                            unchunked.AddRange(session.MessagesWithOrder
                                .Where(stored => !string.IsNullOrWhiteSpace(stored.Message.Content) && !chunked.Contains(stored.Message.PublicId))
                                .Select(stored => (JsonCopy.Out(stored.Message), stored.Order)));
                        }
                    }
                }

                return
                [
                    .. unchunked
                        .OrderBy(m => m.Order)
                        .Take(limit)
                        .Select(m => m.Message),
                ];
            },
            ct);

    public Task AddChunksAsync(IReadOnlyList<MessageChunk> chunks, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(chunks);
                foreach (var chunk in chunks)
                {
                    ArgumentNullException.ThrowIfNull(chunk);
                    ArgumentNullException.ThrowIfNull(chunk.Content);
                    ArgumentOutOfRangeException.ThrowIfNegative(chunk.ChunkIndex);
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(chunk.ChunkIndex, MessageChunk.MaxChunkIndex);
                }

                var now = state.Clock.GetUtcNow();
                foreach (var group in chunks.GroupBy(chunk => chunk.WorkspaceName, StringComparer.Ordinal))
                {
                    if (state.FindWorkspace(group.Key) is not { } workspace)
                    {
                        continue;
                    }

                    lock (workspace.Gate)
                    {
                        foreach (var chunk in group)
                        {
                            if (!workspace.IsDeleting
                                && workspace.Sessions.GetValueOrDefault(chunk.SessionName) is { IsDeleting: false } session
                                && session.ContainsMessage(chunk.MessageId))
                            {
                                session.Chunks.TryAdd(
                                    (chunk.MessageId, chunk.ChunkIndex),
                                    StoredChunk.Pending(chunk.MessageId, chunk.ChunkIndex, chunk.Content, now, state.NextOrder()));
                            }
                        }
                    }
                }
            },
            ct);

    /// <summary>The sessions whose chunks are in the backlog: none of a workspace or session being deleted, whose data is never embedded.</summary>
    private static IEnumerable<SessionEntry> PendingChunkSessions(WorkspaceEntry workspace) =>
        workspace.IsDeleting ? [] : workspace.Sessions.Values.Where(session => !session.IsDeleting);

    /// <summary>A store-owned copy of <paramref name="metadata"/> with the embedding model set; the original is not changed.</summary>
    private static JsonObject WithModel(JsonObject metadata, string embeddingModel)
    {
        var copy = JsonNode.Parse(metadata.ToJsonString())!.AsObject();
        copy[VectorSpace.ModelMetadataKey] = embeddingModel;
        return copy;
    }

    /// <summary>Applies the change only while the conclusion or chunk is still pending with the claimed content.</summary>
    private void Update(
        PendingEmbedding pending,
        Func<StoredConclusion, StoredConclusion> changeConclusion,
        Func<StoredChunk, StoredChunk> changeChunk)
    {
        if (state.FindWorkspace(pending.WorkspaceName) is not { } workspace)
        {
            return;
        }

        lock (workspace.Gate)
        {
            if (pending.Kind == PendingEmbeddingKind.MessageChunk)
            {
                var key = (pending.SourceId, pending.ChunkIndex);
                if (workspace.FindSessionOfMessage(pending.SourceId) is { } session
                    && session.Chunks.TryGetValue(key, out var chunk)
                    && chunk.IsPendingWith(pending.Content))
                {
                    session.Chunks[key] = changeChunk(chunk);
                }
            }
            else if (workspace.Conclusions.TryGetValue(pending.SourceId, out var stored) && stored.IsPendingWith(pending.Content))
            {
                workspace.Conclusions[pending.SourceId] = changeConclusion(stored);
            }
        }
    }
}
