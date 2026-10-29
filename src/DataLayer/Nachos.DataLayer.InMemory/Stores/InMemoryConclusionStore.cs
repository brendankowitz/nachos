using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

/// <remarks>
/// Writes follow the append's discipline: caller input (JSON, vectors) is copied and validated before any lock, every
/// reference is checked under the workspace gate before the first change, and only then is state changed by steps that
/// cannot fail, so an exception leaves nothing behind. <see cref="CommitDerivationAsync"/> also holds the queue gate,
/// which makes the lease check, the writes and the completion one atomic step.
/// </remarks>
internal sealed class InMemoryConclusionStore(InMemoryState state) : IConclusionStore
{
    public Task<CollectionRecord?> GetCollectionAsync(
        string workspaceName, string observerName, string observedName, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return workspace.Collections.GetValueOrDefault((observerName, observedName));
                }
            },
            ct);

    public Task<ConclusionRecord?> FindExactAsync(
        string workspaceName,
        string observerName,
        string observedName,
        ConclusionLevel level,
        string content,
        string? restrictToSession,
        CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var match = workspace.Conclusions.Values
                        .Where(c => c.IsLive
                            && c.InCollection(observerName, observedName)
                            && c.InSession(restrictToSession)
                            && c.Record.Level == level
                            && string.Equals(c.Record.Content, content, StringComparison.Ordinal))
                        .MinBy(c => c.Order);
                    return match is null ? null : JsonCopy.Out(match.Record);
                }
            },
            ct);

    public Task<ConclusionMatch?> FindNearestAsync(
        string workspaceName,
        string observerName,
        string observedName,
        ReadOnlyMemory<float> vector,
        double maxDistance,
        string? restrictToSession,
        CancellationToken ct) =>
        state.Run(
            () =>
            {
                var query = VectorMath.Own(vector, nameof(vector));
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    var candidates = workspace.Conclusions.Values.Where(c =>
                        c.IsSearchable && c.InCollection(observerName, observedName) && c.InSession(restrictToSession));
                    return Nearest(candidates, query, maxDistance).FirstOrDefault();
                }
            },
            ct);

    public Task<Page<ConclusionRecord>> ListAsync(
        string workspaceName, FilterNode? filter, ConclusionOrder order, PageRequest page, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                var prepared = InMemoryFilterEvaluator.Prepare(filter);
                lock (workspace.Gate)
                {
                    var live = workspace.Conclusions.Values
                        .Where(c => c.IsLive && InMemoryFilterEvaluator.Matches(prepared, c.Record));
                    var ordered = order switch
                    {
                        ConclusionOrder.CreatedAt => live.OrderBy(c => c.Record.CreatedAt),
                        ConclusionOrder.TimesDerived => live.OrderBy(c => c.Record.TimesDerived).ThenBy(c => c.Record.CreatedAt),
                        _ => throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown conclusion order."),
                    };
                    return Paging.ToPage(ordered.ThenBy(c => c.Order), page, c => JsonCopy.Out(c.Record));
                }
            },
            ct);

    public Task<IReadOnlyList<ConclusionMatch>> QueryAsync(
        string workspaceName,
        ReadOnlyMemory<float> vector,
        int topK,
        double? maxDistance,
        FilterNode? filter,
        CancellationToken ct) =>
        state.Run<IReadOnlyList<ConclusionMatch>>(
            () =>
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
                var query = VectorMath.Own(vector, nameof(vector));
                var workspace = state.RequireWorkspace(workspaceName);
                var prepared = InMemoryFilterEvaluator.Prepare(filter);
                lock (workspace.Gate)
                {
                    var candidates = workspace.Conclusions.Values
                        .Where(c => c.IsSearchable && InMemoryFilterEvaluator.Matches(prepared, c.Record));
                    return [.. Nearest(candidates, query, maxDistance ?? double.PositiveInfinity).Take(topK)];
                }
            },
            ct);

    public Task<ConclusionRecord?> GetAsync(string workspaceName, string publicId, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return workspace.Conclusions.TryGetValue(publicId, out var stored) && stored.IsLive
                        ? JsonCopy.Out(stored.Record)
                        : null;
                }
            },
            ct);

    public Task CommitDerivationAsync(WorkLease lease, DerivationChanges changes, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(lease);
                ArgumentNullException.ThrowIfNull(changes);
                if (lease.Items.Any(item => !string.Equals(item.WorkspaceName, changes.WorkspaceName, StringComparison.Ordinal)))
                {
                    throw new ArgumentException(
                        $"The changes are for workspace '{changes.WorkspaceName}', but the lease on '{lease.WorkUnitKey}' holds work of another workspace.",
                        nameof(changes));
                }

                var inserts = Own(changes.Inserts);
                var replacements = changes.Replacements
                    .Select(r => (r.ConclusionId, r.Content, Embedding: VectorMath.OwnOptional(r.Embedding, nameof(changes))))
                    .ToList();
                var workspace = state.RequireWorkspace(changes.WorkspaceName);
                lock (workspace.Gate)
                {
                    lock (state.Queue.Gate)
                    {
                        state.Queue.RequireHeld(lease);
                        RequireReferences(workspace, inserts);

                        var now = state.Clock.GetUtcNow();
                        var inserted = Insert(workspace, inserts, now);
                        var touched = inserted.Select(c => (c.ObserverName, c.ObservedName)).ToHashSet();

                        // A replacement is a re-derivation, counted once unless the same id is already corroborated.
                        var corroborated = changes.Corroborations.ToHashSet(StringComparer.Ordinal);
                        var rederived = changes.Corroborations
                            .Concat(replacements.Select(r => r.ConclusionId).Where(id => !corroborated.Contains(id)));
                        foreach (var id in rederived)
                        {
                            Change(workspace, id, touched, c => c with { Record = c.Record with { TimesDerived = c.Record.TimesDerived + 1 } });
                        }

                        foreach (var (id, content, embedding) in replacements)
                        {
                            Change(workspace, id, touched, c => c.WithContent(content, embedding));
                        }

                        foreach (var key in touched)
                        {
                            var explicitInserts = inserted.Count(c =>
                                (c.ObserverName, c.ObservedName) == key && c.Level == ConclusionLevel.Explicit);
                            var counters = workspace.Collections[key];
                            workspace.Collections[key] = counters with
                            {
                                NewExplicitCount = counters.NewExplicitCount + explicitInserts,
                                LastActivityAt = now,
                            };
                        }

                        state.Queue.Complete(lease, now);
                    }
                }
            },
            ct);

    public Task<IReadOnlyList<ConclusionRecord>> InsertAsync(
        string workspaceName, IReadOnlyList<NewConclusion> conclusions, CancellationToken ct) =>
        state.Run<IReadOnlyList<ConclusionRecord>>(
            () =>
            {
                var owned = Own(conclusions);
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    RequireReferences(workspace, owned);
                    return [.. Insert(workspace, owned, state.Clock.GetUtcNow()).Select(JsonCopy.Out)];
                }
            },
            ct);

    public Task TombstoneAsync(string workspaceName, string publicId, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    Change(workspace, publicId, touched: null, c => c with { DeletedAt = state.Clock.GetUtcNow() });
                }
            },
            ct);

    public Task<int> PurgeTombstonesAsync(TimeSpan retention, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var cutoff = state.Clock.GetUtcNow() - retention;
                var purged = 0;
                foreach (var workspace in state.AllWorkspaces())
                {
                    lock (workspace.Gate)
                    {
                        foreach (var id in workspace.Conclusions.Values.Where(c => c.DeletedAt <= cutoff).Select(c => c.Record.PublicId).ToList())
                        {
                            workspace.Conclusions.Remove(id);
                            purged++;
                        }
                    }
                }

                return purged;
            },
            ct);

    /// <summary>Nearest first, ties oldest first, leaving out those farther than <paramref name="maxDistance"/>.</summary>
    private static IEnumerable<ConclusionMatch> Nearest(IEnumerable<StoredConclusion> candidates, float[] query, double maxDistance) =>
        candidates
            .Select(c => (Conclusion: c, Distance: VectorMath.CosineDistance(c.Embedding!, query)))
            .Where(pair => pair.Distance <= maxDistance)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Conclusion.Record.CreatedAt)
            .ThenBy(pair => pair.Conclusion.Order)
            .Select(pair => new ConclusionMatch(JsonCopy.Out(pair.Conclusion.Record), pair.Distance));

    /// <summary>Store-owned, validated copies of the caller's conclusions; no lock is held.</summary>
    private static List<OwnedConclusion> Own(IReadOnlyList<NewConclusion> conclusions)
    {
        ArgumentNullException.ThrowIfNull(conclusions);
        return
        [
            .. conclusions.Select(c => new OwnedConclusion(
                c,
                JsonCopy.Own(c.InternalMetadata, "internal_metadata"),
                VectorMath.OwnOptional(c.Embedding, nameof(conclusions)),
                [.. c.SourceIds])),
        ];
    }

    /// <exception cref="NotFoundException">A conclusion names a peer or session that does not exist.</exception>
    private static void RequireReferences(WorkspaceEntry workspace, List<OwnedConclusion> conclusions)
    {
        foreach (var conclusion in conclusions)
        {
            workspace.RequirePeer(conclusion.Source.ObserverName);
            workspace.RequirePeer(conclusion.Source.ObservedName);
            workspace.RequireSessionExists(conclusion.Source.SessionName);
        }
    }

    /// <summary>Inserts validated conclusions, creating their collections; cannot fail.</summary>
    private List<ConclusionRecord> Insert(WorkspaceEntry workspace, List<OwnedConclusion> conclusions, DateTimeOffset now)
    {
        var inserted = new List<ConclusionRecord>(conclusions.Count);
        foreach (var (source, metadata, embedding, sourceIds) in conclusions)
        {
            workspace.GetOrAddCollection(source.ObserverName, source.ObservedName, now);
            var record = new ConclusionRecord(
                NewPublicId(workspace),
                workspace.Name,
                source.ObserverName,
                source.ObservedName,
                source.SessionName,
                source.Level,
                source.Content,
                TimesDerived: 1,
                sourceIds,
                metadata,
                now);
            var embeddingState = embedding is null ? EmbeddingState.Pending : EmbeddingState.Synced;
            workspace.Conclusions.Add(
                record.PublicId, new StoredConclusion(record, embedding, embeddingState, DeletedAt: null, state.NextOrder()));
            inserted.Add(record);
        }

        return inserted;
    }

    /// <summary>
    /// Replaces a live conclusion through <paramref name="change"/> and records its collection in
    /// <paramref name="touched"/>; a missing or tombstoned one is skipped.
    /// </summary>
    private static void Change(
        WorkspaceEntry workspace,
        string publicId,
        HashSet<(string ObserverName, string ObservedName)>? touched,
        Func<StoredConclusion, StoredConclusion> change)
    {
        if (workspace.Conclusions.TryGetValue(publicId, out var stored) && stored.IsLive)
        {
            workspace.Conclusions[publicId] = change(stored);
            touched?.Add((stored.Record.ObserverName, stored.Record.ObservedName));
        }
    }

    /// <summary>A public id unused in the workspace, so the insert cannot fail on a (astronomically unlikely) collision.</summary>
    private static string NewPublicId(WorkspaceEntry workspace)
    {
        string id;
        do
        {
            id = PublicId.New();
        }
        while (workspace.Conclusions.ContainsKey(id));

        return id;
    }

    private sealed record OwnedConclusion(NewConclusion Source, JsonObject Metadata, float[]? Embedding, string[] SourceIds);
}
