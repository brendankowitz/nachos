using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;

namespace Nachos.Core.Memory;

/// <summary>
/// Builds the working representation one peer holds of another (spec §11.4) from stored conclusions, without a model
/// call.
/// </summary>
/// <remarks>
/// <para>
/// Three sources, in rank order: the semantic matches of the search query (only when one is given), the most frequently
/// derived conclusions (only when asked for), and the most recent ones. A conclusion found by several sources keeps its
/// best rank. When there are more than <see cref="RepresentationQuery.MaxConclusions"/>, the best-ranked survive, and
/// within a source its own order decides (nearest, most derived, newest).
/// </para>
/// <para>
/// The output is one Markdown section per level that has conclusions, in level order, each a bullet list ordered by
/// rank, then creation time, then id, so the same memory always renders the same text.
/// </para>
/// </remarks>
public sealed class RepresentationBuilder(
    IMemoryStore store,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings)
{
    /// <summary>The Markdown representation <paramref name="observerName"/> holds of <paramref name="observedName"/>.</summary>
    public async Task<string> BuildAsync(
        string workspaceName,
        string observerName,
        string observedName,
        VisibilityPolicy policy,
        RepresentationQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(query);
        var filter = policy.Restrict(Collection(observerName, observedName, query.Filter));
        var sources = new List<IReadOnlyList<ConclusionRecord>>(3);
        if (query.SearchQuery is not null)
        {
            var vector = await QueryEmbedding.EmbedAsync(embeddings, query.SearchQuery, ct).ConfigureAwait(false);
            var matches = await store.Conclusions.QueryAsync(
                workspaceName, vector, query.SearchTopK, query.SearchMaxDistance, filter, ct).ConfigureAwait(false);
            sources.Add([.. matches.Select(m => m.Conclusion)]);
        }

        var newest = new PageRequest(Size: query.MaxConclusions, Reverse: true);
        if (query.IncludeMostFrequent)
        {
            sources.Add((await store.Conclusions.ListAsync(workspaceName, filter, ConclusionOrder.TimesDerived, newest, ct)
                .ConfigureAwait(false)).Items);
        }

        sources.Add((await store.Conclusions.ListAsync(workspaceName, filter, ConclusionOrder.CreatedAt, newest, ct)
            .ConfigureAwait(false)).Items);

        return Format(Merge(sources, policy, query.MaxConclusions));
    }

    private static FilterNode.And Collection(string observerName, string observedName, FilterNode? filter)
    {
        FilterNode observer = new FilterNode.Field(FilterColumns.ObserverId, FilterOp.Eq, JsonValue.Create(observerName));
        FilterNode observed = new FilterNode.Field(FilterColumns.ObservedId, FilterOp.Eq, JsonValue.Create(observedName));
        return new FilterNode.And(filter is null ? [observer, observed] : [observer, observed, filter]);
    }

    private static List<(int Rank, ConclusionRecord Conclusion)> Merge(
        List<IReadOnlyList<ConclusionRecord>> sources, VisibilityPolicy policy, int maxConclusions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<(int Rank, ConclusionRecord Conclusion)>(maxConclusions);
        for (var rank = 0; rank < sources.Count && kept.Count < maxConclusions; rank++)
        {
            foreach (var conclusion in sources[rank])
            {
                if (kept.Count == maxConclusions)
                {
                    break;
                }

                if (policy.Allows(conclusion) && seen.Add(conclusion.PublicId))
                {
                    kept.Add((rank, conclusion));
                }
            }
        }

        return kept;
    }

    private static string Format(List<(int Rank, ConclusionRecord Conclusion)> conclusions)
    {
        var text = new StringBuilder();
        var sections = conclusions
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.Conclusion.CreatedAt)
            .ThenBy(c => c.Conclusion.PublicId, StringComparer.Ordinal)
            .GroupBy(c => c.Conclusion.Level)
            .OrderBy(section => section.Key);
        foreach (var section in sections)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append("## ").Append(section.Key).Append("\n\n");
            foreach (var (_, conclusion) in section)
            {
                // API-created content may span lines; one line per bullet keeps the list structure intact.
                text.Append("- ").Append(conclusion.Content.ReplaceLineEndings(" ")).Append('\n');
            }
        }

        return text.ToString();
    }
}
