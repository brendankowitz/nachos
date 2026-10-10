using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Domain;

/// <summary>A stored workspace. <paramref name="Name"/> is the public, case-sensitive id.</summary>
public sealed record WorkspaceRecord(
    string Name,
    JsonObject Metadata,
    JsonObject Configuration,
    LifecycleState State,
    DateTimeOffset CreatedAt);