using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A session's context within a token budget (<c>GET S/context</c>).</summary>
/// <param name="Id">The session id.</param>
/// <param name="Messages">The newest messages that fit, in chronological order, all after the summary's coverage.</param>
/// <param name="Summary">The summary that fits, or null.</param>
/// <param name="PeerRepresentation">The working representation, or null when no <c>peer_target</c> was given.</param>
/// <param name="PeerCard">The card's entries, or null when there is none, it is hidden or it did not fit.</param>
public sealed record SessionContext(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("messages")] IReadOnlyList<Message> Messages,
    [property: JsonPropertyName("summary")] Summary? Summary,
    [property: JsonPropertyName("peer_representation")] string? PeerRepresentation,
    [property: JsonPropertyName("peer_card")] IReadOnlyList<string>? PeerCard);
