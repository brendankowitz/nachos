namespace Nachos.Core.Configuration;

/// <summary>Message search tuning (spec §11.5). The limits of a request (<c>limit</c> 1 to 100) are wire rules, not options.</summary>
public sealed class SearchOptions
{
    /// <summary>The default <see cref="MaxVectorCandidates"/>, chosen from the M3 search benchmark (research/m3-search-benchmark.md).</summary>
    public const int DefaultMaxVectorCandidates = 100_000;

    /// <summary>
    /// The most embedded chunks one workspace-scoped vector search compares, the most recently stored first; default
    /// 100,000. Exact vector search reads every chunk in scope, so a workspace search slows down as the workspace grows; the
    /// cap bounds it, at the cost that older messages of a large workspace are missed by the vector half (lexical search
    /// still finds them). Session and peer searches are not capped. Null compares every chunk.
    /// </summary>
    public int? MaxVectorCandidates { get; set; } = DefaultMaxVectorCandidates;
}
