using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;

namespace Nachos.Core.Deriver;

/// <summary>
/// Decides, for one collection, which of a batch's explicit facts are new and which re-derive a conclusion the
/// collection already holds (spec §11.2). Explicit facts only ever match conclusions of the same session.
/// </summary>
/// <remarks>
/// A fact is a duplicate when its normalized content equals an existing conclusion's, or when its embedding is within
/// <see cref="DeriverOptions.DedupMaxDistance"/> of one. A duplicate corroborates the conclusion, or replaces its content
/// when the fact is richer (see <see cref="IsRicher"/>). Facts of the batch are also checked against each other, so
/// duplicates within one batch insert once. Reads only; nothing is written until the caller commits the result.
/// </remarks>
public sealed partial class ConclusionDeduplicator
{
    private readonly IMemoryStore _store;
    private readonly double _maxDistance;

    public ConclusionDeduplicator(IMemoryStore store, IOptions<NachosOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _maxDistance = options.Value.Deriver.DedupMaxDistance;
    }

    /// <summary>The stored and compared form of a fact: NFC, trimmed, with every whitespace run collapsed to one space.</summary>
    public static string Normalize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Whitespace().Replace(content.Normalize(NormalizationForm.FormC), " ").Trim();
    }

    /// <summary>
    /// Deduplicates <paramref name="facts"/> against the collection of <paramref name="observerName"/> about
    /// <paramref name="observedName"/>, restricted to <paramref name="sessionName"/>.
    /// </summary>
    public async Task<DeduplicationResult> DeduplicateAsync(
        string workspaceName,
        string observerName,
        string observedName,
        string sessionName,
        IReadOnlyList<DerivedFact> facts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var inserts = new List<DerivedFact>();
        // Each matched conclusion is counted once per batch: null corroborates it, a value replaces its content.
        var matched = new Dictionary<string, ConclusionReplacement?>(StringComparer.Ordinal);
        var matchedOrder = new List<string>();

        foreach (var fact in facts)
        {
            var existing = await FindExistingAsync(workspaceName, observerName, observedName, sessionName, fact, ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!matched.TryGetValue(existing.PublicId, out var replacement))
                {
                    matchedOrder.Add(existing.PublicId);
                }

                var best = replacement?.Content ?? existing.Content;
                matched[existing.PublicId] = IsRicher(fact.Content, best)
                    ? new ConclusionReplacement(existing.PublicId, fact.Content, fact.Embedding)
                    : replacement;
                continue;
            }

            var pending = inserts.FindIndex(insert => IsSameFact(insert, fact));
            if (pending < 0)
            {
                inserts.Add(fact);
                continue;
            }

            var kept = inserts[pending];
            var sources = kept.SourceMessageIds.Union(fact.SourceMessageIds, StringComparer.Ordinal).ToList();
            inserts[pending] = IsRicher(fact.Content, kept.Content)
                ? fact with { SourceMessageIds = sources }
                : kept with { SourceMessageIds = sources };
        }

        return new DeduplicationResult(
            inserts,
            [.. matchedOrder.Where(id => matched[id] is null)],
            [.. matchedOrder.Select(id => matched[id]).OfType<ConclusionReplacement>()]);
    }

    private async Task<ConclusionRecord?> FindExistingAsync(
        string workspaceName,
        string observerName,
        string observedName,
        string sessionName,
        DerivedFact fact,
        CancellationToken ct)
    {
        var exact = await _store.Conclusions.FindExactAsync(
            workspaceName, observerName, observedName, ConclusionLevel.Explicit, fact.Content, sessionName, ct)
            .ConfigureAwait(false);
        if (exact is not null)
        {
            return exact;
        }

        var nearest = await _store.Conclusions.FindNearestAsync(
            workspaceName, observerName, observedName, ConclusionLevel.Explicit, fact.Embedding, _maxDistance, sessionName, ct)
            .ConfigureAwait(false);
        return nearest?.Conclusion;
    }

    private bool IsSameFact(DerivedFact a, DerivedFact b) =>
        string.Equals(a.Content, b.Content, StringComparison.Ordinal)
        || CosineDistance(a.Embedding.Span, b.Embedding.Span) <= _maxDistance;

    /// <summary>
    /// A fact is richer than another when it is longer and keeps every word of it, so replacing loses nothing.
    /// Words compare case-insensitively.
    /// </summary>
    private static bool IsRicher(string candidate, string current) =>
        candidate.Length > current.Length && Words(current).IsSubsetOf(Words(candidate));

    private static HashSet<string> Words(string text) =>
        Word().Matches(text).Select(match => match.Value.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

    private static double CosineDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 1 : 1 - (dot / Math.Sqrt(normA * normB));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
