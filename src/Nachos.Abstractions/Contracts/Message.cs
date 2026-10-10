using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A message as exposed on the wire.</summary>
public sealed record Message(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("peer_id")] string PeerId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("metadata")] JsonObject Metadata,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("token_count")] int TokenCount);