using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Domain;

/// <summary>A stored peer. <paramref name="Name"/> is the public, case-sensitive id.</summary>
public sealed record PeerRecord(
    string WorkspaceName,
    string Name,
    JsonObject Metadata,
    JsonObject Configuration,
    bool IsInternal,
    DateTimeOffset CreatedAt);