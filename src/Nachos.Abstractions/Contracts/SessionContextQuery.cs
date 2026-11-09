using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// The query parameters of <c>GET S/context</c>. Every value is optional. The representation members mean what the same
/// members of <see cref="PeerContextQuery"/> mean, with the same bounds.
/// </summary>
/// <param name="Tokens">The budget in content tokens, 1 to 100,000; 100,000 when absent. The context never exceeds it.</param>
/// <param name="Summary">Whether to include the session's summary; true when absent.</param>
/// <param name="SearchQuery">Adds the conclusions nearest to this text to the representation.</param>
/// <param name="PeerTarget">The observed peer. The representation and card are included only when it is set.</param>
/// <param name="PeerPerspective">The observer; <paramref name="PeerTarget"/> itself when absent. Requires <paramref name="PeerTarget"/>.</param>
/// <param name="Scope">A scope id. Scopes are not supported yet, so any value is a 422.</param>
/// <param name="Sessions">Sessions added to the visibility allowlist.</param>
/// <param name="LimitToSession">Adds this session to the visibility allowlist.</param>
/// <param name="SearchTopK">How many semantic matches of <paramref name="SearchQuery"/> to add, 1 to 100.</param>
/// <param name="SearchMaxDistance">The largest cosine distance of a semantic match, 0 to 1.</param>
/// <param name="IncludeMostFrequent">Adds the most frequently derived conclusions to the representation.</param>
/// <param name="MaxConclusions">How many conclusions the representation holds at most, 1 to 100.</param>
public sealed record SessionContextQuery(
    [property: JsonPropertyName("tokens")] int? Tokens = null,
    [property: JsonPropertyName("summary")] bool? Summary = null,
    [property: JsonPropertyName("search_query")] string? SearchQuery = null,
    [property: JsonPropertyName("peer_target")] string? PeerTarget = null,
    [property: JsonPropertyName("peer_perspective")] string? PeerPerspective = null,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("sessions")] IReadOnlyList<string>? Sessions = null,
    [property: JsonPropertyName("limit_to_session")] bool? LimitToSession = null,
    [property: JsonPropertyName("search_top_k")] int? SearchTopK = null,
    [property: JsonPropertyName("search_max_distance")] double? SearchMaxDistance = null,
    [property: JsonPropertyName("include_most_frequent")] bool? IncludeMostFrequent = null,
    [property: JsonPropertyName("max_conclusions")] int? MaxConclusions = null);
