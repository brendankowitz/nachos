using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;

namespace Nachos.Api.Json;

public sealed record WorkspaceCreate(string Id, JsonObject? Metadata = null, WorkspaceConfiguration? Configuration = null);
public sealed record WorkspaceUpdate(JsonObject? Metadata = null, WorkspaceConfiguration? Configuration = null);
public sealed record PeerCreate(string Id, JsonObject? Metadata = null, JsonObject? Configuration = null);
public sealed record PeerUpdate(JsonObject? Metadata = null, JsonObject? Configuration = null);
public sealed record PeerGet(JsonObject? Filters = null, string? Kind = null);
public sealed record SessionCreate(string Id, JsonObject? Metadata = null, SessionConfiguration? Configuration = null,
    IReadOnlyDictionary<string, SessionPeerConfig>? Peers = null, IReadOnlyList<string>? Scopes = null);
public sealed record SessionUpdate(JsonObject? Metadata = null, SessionConfiguration? Configuration = null);
public sealed record ResourceGet(JsonObject? Filters = null);
public sealed record MessageUpdate(JsonObject? Metadata = null);
public sealed record MessageBatchCreate(IReadOnlyList<MessageCreate> Messages);
