using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>The body of <c>POST P/representation</c>. Every value is optional.</summary>
/// <param name="SessionId">Restricts the representation to explicit conclusions stated in this session.</param>
/// <param name="Target">The observed peer; the peer itself when null.</param>
/// <param name="SearchQuery">Adds the conclusions semantically nearest to this text.</param>
/// <param name="SearchTopK">How many semantic matches to add, 1 to 100.</param>
/// <param name="SearchMaxDistance">The largest cosine distance (0 to 1) of a semantic match.</param>
/// <param name="IncludeMostFrequent">Adds the most frequently derived conclusions.</param>
/// <param name="MaxConclusions">How many conclusions the representation holds at most, 1 to 100.</param>
/// <param name="Filters">A conclusion filter applied to every source.</param>
/// <param name="Scope">A scope id or a list of them. Scopes are not supported yet, so any value is a 422.</param>
public sealed record PeerRepresentationGet(
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("search_query")] string? SearchQuery = null,
    [property: JsonPropertyName("search_top_k")] int? SearchTopK = null,
    [property: JsonPropertyName("search_max_distance")] double? SearchMaxDistance = null,
    [property: JsonPropertyName("include_most_frequent")] bool? IncludeMostFrequent = null,
    [property: JsonPropertyName("max_conclusions")] int? MaxConclusions = null,
    [property: JsonPropertyName("filters")] JsonObject? Filters = null,
    [property: JsonPropertyName("scope")] JsonNode? Scope = null);
