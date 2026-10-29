using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// What <paramref name="PeerId"/> knows about <paramref name="TargetId"/>: the working representation and the peer card.
/// </summary>
/// <param name="TargetId">The observed peer; <paramref name="PeerId"/> itself when no target was given.</param>
/// <param name="PeerCard">The card's entries, or null when there is no card.</param>
public sealed record PeerContext(
    [property: JsonPropertyName("peer_id")] string PeerId,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("representation")] string? Representation,
    [property: JsonPropertyName("peer_card")] IReadOnlyList<string>? PeerCard);
