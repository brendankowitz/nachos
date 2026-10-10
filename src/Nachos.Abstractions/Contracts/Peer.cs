using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A peer as exposed on the wire.</summary>
public sealed record Peer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("metadata")] JsonObject Metadata,
    [property: JsonPropertyName("configuration")] JsonObject Configuration);