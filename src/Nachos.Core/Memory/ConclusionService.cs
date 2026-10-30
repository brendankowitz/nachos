using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.Core.Llm;

namespace Nachos.Core.Memory;

/// <summary>
/// Conclusion reads and API writes. Every read takes the request's <see cref="VisibilityPolicy"/>; inputs are trusted
/// (the caller validated them).
/// </summary>
/// <remarks>
/// The embedding generator is optional so that a host without AI still serves everything else: created conclusions
/// then stay pending, and a semantic query throws <see cref="ServiceUnavailableException"/>. Logging is optional because
/// a bare container may register none.
/// </remarks>
public sealed partial class ConclusionService(
    IMemoryStore store,
    ILogger<ConclusionService>? logger = null,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ConclusionService>.Instance;

    /// <summary>
    /// Inserts explicit conclusions, all or nothing and without deduplication, with no source ids (owner decision Q5).
    /// They are stored pending and then embedded best-effort; one that is not embedded here is embedded by the
    /// reconciler, which is the guarantee.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace, a peer or a session does not exist.</exception>
    public async Task<IReadOnlyList<ConclusionRecord>> CreateAsync(
        string workspaceName, IReadOnlyList<ConclusionCreate> conclusions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conclusions);
        var inserts = conclusions
            .Select(c => new NewConclusion(c.ObserverId, c.ObservedId, c.SessionId, ConclusionLevel.Explicit, c.Content,
                Embedding: null, SourceIds: [], InternalMetadata: null))
            .ToArray();
        var created = await store.Conclusions.InsertAsync(workspaceName, inserts, ct).ConfigureAwait(false);
        await TryEmbedAsync(workspaceName, created, ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>The visible conclusions matching <paramref name="filter"/>, in creation order.</summary>
    public async Task<Page<ConclusionRecord>> ListAsync(
        string workspaceName, VisibilityPolicy policy, FilterNode? filter, PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var listed = await store.Conclusions.ListAsync(workspaceName, policy.Restrict(filter), ConclusionOrder.CreatedAt, page, ct)
            .ConfigureAwait(false);
        return listed with { Items = [.. listed.Items.Where(policy.Allows)] };
    }

    /// <summary>
    /// The <paramref name="topK"/> visible conclusions nearest to <paramref name="query"/>, nearest first, none farther
    /// than <paramref name="maxDistance"/> when it is set.
    /// </summary>
    public async Task<IReadOnlyList<ConclusionRecord>> QueryAsync(
        string workspaceName,
        VisibilityPolicy policy,
        string query,
        int topK,
        double? maxDistance,
        FilterNode? filter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var vector = await QueryEmbedding.EmbedAsync(embeddings, query, ct).ConfigureAwait(false);
        var matches = await store.Conclusions.QueryAsync(workspaceName, vector, topK, maxDistance, policy.Restrict(filter), ct)
            .ConfigureAwait(false);
        return [.. matches.Select(m => m.Conclusion).Where(policy.Allows)];
    }

    /// <summary>The conclusion, or null when it is missing, deleted or not visible.</summary>
    public async Task<ConclusionRecord?> GetAsync(
        string workspaceName, VisibilityPolicy policy, string conclusionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var conclusion = await store.Conclusions.GetAsync(workspaceName, conclusionId, ct).ConfigureAwait(false);
        return conclusion is not null && policy.Allows(conclusion) ? conclusion : null;
    }

    /// <summary>Tombstones the conclusion; a missing one is a no-op.</summary>
    public Task DeleteAsync(string workspaceName, string conclusionId, CancellationToken ct) =>
        store.Conclusions.TombstoneAsync(workspaceName, conclusionId, ct);

    private async Task TryEmbedAsync(string workspaceName, IReadOnlyList<ConclusionRecord> created, CancellationToken ct)
    {
        if (embeddings is null)
        {
            return;
        }

        try
        {
            var vectors = await embeddings.GenerateAsync(created.Select(c => c.Content), cancellationToken: ct).ConfigureAwait(false);
            if (vectors.Count != created.Count || vectors.Any(v => v.Vector.Length != VectorSpace.Dimensions))
            {
                LogUnusableEmbeddings(_logger, created.Count, VectorSpace.Dimensions);
                return;
            }

            var model = EmbeddingModelName.Of(embeddings);
            for (var i = 0; i < created.Count; i++)
            {
                var pending = new PendingEmbedding(workspaceName, created[i].PublicId, created[i].Content);
                await store.Embeddings.SaveAsync(pending, vectors[i].Vector, model, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The conclusions are committed; failing the request now would invite a duplicating retry. A provider
            // timeout surfaces as a TaskCanceledException, so only the caller's own cancellation propagates.
            LogEmbeddingDeferred(_logger, ex, created.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding {Count} created conclusions failed; the reconciler will embed them.")]
    private static partial void LogEmbeddingDeferred(ILogger logger, Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The embedding generator returned unusable vectors for {Count} created conclusions (expected {Dimensions} dimensions each); the reconciler will retry them.")]
    private static partial void LogUnusableEmbeddings(ILogger logger, int count, int dimensions);
}
