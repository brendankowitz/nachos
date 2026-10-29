using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A peer card: its entries in order, or null when there is none.</summary>
public sealed record PeerCardResponse(
    [property: JsonPropertyName("peer_card")] IReadOnlyList<string>? PeerCard);
