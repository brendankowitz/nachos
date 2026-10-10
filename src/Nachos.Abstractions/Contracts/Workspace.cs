using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A workspace as exposed on the wire.</summary>
public sealed record Workspace(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("metadata")] JsonObject Metadata,
    [property: JsonPropertyName("configuration")] JsonObject Configuration,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);