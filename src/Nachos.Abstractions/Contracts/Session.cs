using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// A session as exposed on the wire. <c>Id</c> is the client-chosen name; <c>Uid</c> is the immutable identifier the
/// server assigns. <c>Scopes</c> names the scopes the session is an active member of, and is null unless a session
/// list asked for them.
/// </summary>
public sealed record Session(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("metadata")] JsonObject Metadata,
    [property: JsonPropertyName("configuration")] JsonObject Configuration,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes = null);