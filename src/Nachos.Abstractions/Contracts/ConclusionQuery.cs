using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>The body of a semantic conclusion query (<c>POST C/query</c>).</summary>
/// <param name="Query">The text whose embedding the conclusions are compared with.</param>
/// <param name="TopK">How many conclusions to return at most, 1 to 100.</param>
/// <param name="Distance">When set, the largest cosine distance (0 to 1) a returned conclusion may have.</param>
/// <param name="Filters">A conclusion filter (<c>id</c>, <c>observer_id</c>, <c>observed_id</c>, <c>session_id</c>, <c>created_at</c>).</param>
public sealed record ConclusionQuery(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("top_k")] int TopK = ConclusionQuery.DefaultTopK,
    [property: JsonPropertyName("distance")] double? Distance = null,
    [property: JsonPropertyName("filters")] JsonObject? Filters = null)
{
    public const int DefaultTopK = 10;
}
