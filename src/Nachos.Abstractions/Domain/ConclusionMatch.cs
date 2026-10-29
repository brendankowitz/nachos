namespace Nachos.Abstractions.Domain;

/// <summary>A conclusion found by vector search, with its cosine distance (0 identical, 2 opposite) to the query.</summary>
public sealed record ConclusionMatch(ConclusionRecord Conclusion, double Distance);
