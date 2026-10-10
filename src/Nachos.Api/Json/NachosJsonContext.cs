using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nachos.Abstractions.Contracts;

namespace Nachos.Api.Json;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never, MaxDepth = int.MaxValue)]
[JsonSerializable(typeof(Workspace))]
[JsonSerializable(typeof(Peer))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Message[]))]
[JsonSerializable(typeof(Page<Workspace>))]
[JsonSerializable(typeof(Page<Peer>))]
[JsonSerializable(typeof(Page<Session>))]
[JsonSerializable(typeof(Page<Message>))]
[JsonSerializable(typeof(WorkspaceConfiguration))]
[JsonSerializable(typeof(SessionConfiguration))]
[JsonSerializable(typeof(SessionPeerConfig))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ValidationErrorResponse))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, SessionPeerConfig>))]
[JsonSerializable(typeof(WorkspaceCreate))]
[JsonSerializable(typeof(WorkspaceUpdate))]
[JsonSerializable(typeof(PeerCreate))]
[JsonSerializable(typeof(PeerUpdate))]
[JsonSerializable(typeof(PeerGet))]
[JsonSerializable(typeof(SessionCreate))]
[JsonSerializable(typeof(SessionUpdate))]
[JsonSerializable(typeof(ResourceGet))]
[JsonSerializable(typeof(MessageUpdate))]
[JsonSerializable(typeof(MessageBatchCreate))]
[JsonSerializable(typeof(GrantCreate))]
[JsonSerializable(typeof(KeyResponse))]
public partial class NachosJsonContext : JsonSerializerContext;
