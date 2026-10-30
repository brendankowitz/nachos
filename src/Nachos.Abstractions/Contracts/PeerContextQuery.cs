using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// The query parameters of <c>GET P/context</c>. Every value is optional; they mean what the same members of
/// <see cref="PeerRepresentationGet"/> mean, with the same bounds.
/// </summary>
public sealed record PeerContextQuery(
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("search_query")] string? SearchQuery = null,
    [property: JsonPropertyName("search_top_k")] int? SearchTopK = null,
    [property: JsonPropertyName("search_max_distance")] double? SearchMaxDistance = null,
    [property: JsonPropertyName("include_most_frequent")] bool? IncludeMostFrequent = null,
    [property: JsonPropertyName("max_conclusions")] int? MaxConclusions = null);
