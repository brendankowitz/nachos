using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A session as exposed on the wire.</summary>
public sealed record Session(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("metadata")] JsonObject Metadata,
    [property: JsonPropertyName("configuration")] JsonObject Configuration,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);