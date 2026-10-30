using Nachos.Abstractions.Filtering;

namespace Nachos.Core.Memory;

/// <summary>What a working representation draws on. The caller has validated the bounds (each count 1 to 100, distance 0 to 1).</summary>
/// <param name="MaxConclusions">How many conclusions the representation holds at most.</param>
/// <param name="IncludeMostFrequent">Adds the most frequently derived conclusions.</param>
/// <param name="SearchQuery">Adds the <paramref name="SearchTopK"/> conclusions nearest to this text; null skips the search.</param>
/// <param name="SearchMaxDistance">The largest cosine distance of a semantic match, or null for no limit.</param>
/// <param name="Filter">A conclusion filter applied to every source.</param>
public sealed record RepresentationQuery(
    int MaxConclusions = RepresentationQuery.DefaultMaxConclusions,
    bool IncludeMostFrequent = false,
    string? SearchQuery = null,
    int SearchTopK = RepresentationQuery.DefaultSearchTopK,
    double? SearchMaxDistance = null,
    FilterNode? Filter = null)
{
    public const int DefaultMaxConclusions = 25;

    public const int DefaultSearchTopK = 10;
}
