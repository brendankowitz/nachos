using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;

namespace Nachos.Api.Json;

public sealed record WorkspaceCreate(
    [property: StringLength(512, MinimumLength = 1), RegularExpression("^[a-zA-Z0-9_-]+$")] string Id)
{
    public JsonObject Metadata { get; init; } = new();
    public WorkspaceConfiguration Configuration { get; init; } = new();
}
public sealed record WorkspaceUpdate(JsonObject? Metadata = null, WorkspaceConfiguration? Configuration = null);
public sealed record PeerCreate(
    [property: StringLength(512, MinimumLength = 1), RegularExpression("^[a-zA-Z0-9_-]+$")] string Id,
    JsonObject? Metadata = null, JsonObject? Configuration = null);
public sealed record PeerUpdate(JsonObject? Metadata = null, JsonObject? Configuration = null);
public sealed record PeerGet(JsonObject? Filters = null, string? Kind = null);
public sealed record SessionCreate(
    [property: StringLength(512, MinimumLength = 1), RegularExpression("^[a-zA-Z0-9_-]+$")] string Id,
    JsonObject? Metadata = null, SessionConfiguration? Configuration = null,
    IReadOnlyDictionary<string, SessionPeerConfig>? Peers = null,
    [property: MaxLength(100)] IReadOnlyList<string>? Scopes = null);
public sealed record SessionUpdate(JsonObject? Metadata = null, SessionConfiguration? Configuration = null);
public sealed record ResourceGet(JsonObject? Filters = null);
public sealed record MessageUpdate(JsonObject? Metadata = null);
public sealed record MessageBatchCreate(
    [property: MinLength(1), MaxLength(100)] IReadOnlyList<MessageCreate> Messages);
