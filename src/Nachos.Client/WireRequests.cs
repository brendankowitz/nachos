using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nachos.Abstractions.Contracts;

namespace Nachos.Client;

// Request bodies, named after their wire-manifest schemas. Null members are omitted on the wire
// (NachosHttpClient's serializer ignores nulls), which is how "unchanged" / "inherit" / "no filter" is expressed.

/// <summary><c>WorkspaceCreate</c>.</summary>
internal sealed record WorkspaceCreate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("metadata")] JsonObject? Metadata,
    [property: JsonPropertyName("configuration")] WorkspaceConfiguration? Configuration);

/// <summary><c>WorkspaceUpdate</c>.</summary>
internal sealed record WorkspaceUpdate(
    [property: JsonPropertyName("metadata")] JsonObject? Metadata,
    [property: JsonPropertyName("configuration")] WorkspaceConfiguration? Configuration);

/// <summary><c>PeerCreate</c>.</summary>
internal sealed record PeerCreate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("metadata")] JsonObject? Metadata,
    [property: JsonPropertyName("configuration")] JsonObject? Configuration);

/// <summary><c>PeerUpdate</c>.</summary>
internal sealed record PeerUpdate(
    [property: JsonPropertyName("metadata")] JsonObject? Metadata,
    [property: JsonPropertyName("configuration")] JsonObject? Configuration);

/// <summary><c>PeerGet</c>: <c>kind</c> is absent for regular peers, else <c>"scope"</c> or <c>"all"</c>.</summary>
internal sealed record PeerGet(
    [property: JsonPropertyName("filters")] JsonObject? Filters,
    [property: JsonPropertyName("kind")] string? Kind);

/// <summary><c>SessionCreate</c>.</summary>
internal sealed record SessionCreate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("metadata")] JsonObject? Metadata,
    [property: JsonPropertyName("configuration")] SessionConfiguration? Configuration,
    [property: JsonPropertyName("peers")] IReadOnlyDictionary<string, SessionPeerConfig>? Peers);

/// <summary><c>SessionUpdate</c>.</summary>
internal sealed record SessionUpdate(
    [property: JsonPropertyName("metadata")] JsonObject? Metadata,
    [property: JsonPropertyName("configuration")] SessionConfiguration? Configuration);

/// <summary>The filter-only list bodies: <c>WorkspaceGet</c>, <c>SessionGet</c>, <c>MessageGet</c>.</summary>
internal sealed record FilterBody(
    [property: JsonPropertyName("filters")] JsonObject? Filters);

/// <summary><c>MessageBatchCreate</c>.</summary>
internal sealed record MessageBatchCreate(
    [property: JsonPropertyName("messages")] IReadOnlyList<MessageCreate> Messages);

/// <summary><c>MessageUpdate</c>.</summary>
internal sealed record MessageUpdate(
    [property: JsonPropertyName("metadata")] JsonObject? Metadata);

/// <summary>Body of the Nachos extension route <c>POST /v3/admin/grants</c>.</summary>
internal sealed record GrantCreate(
    [property: JsonPropertyName("object_id")] string ObjectId,
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("role")] string Role);
