using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>One entry of a message batch create request.</summary>
public sealed record MessageCreate(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("peer_id")] string PeerId,
    [property: JsonPropertyName("metadata")] JsonObject? Metadata = null,
    [property: JsonPropertyName("configuration")] MessageConfiguration? Configuration = null,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null);