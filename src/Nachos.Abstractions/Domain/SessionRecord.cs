using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Domain;

/// <summary>A stored session. <paramref name="Name"/> is the public, case-sensitive id.</summary>
public sealed record SessionRecord(
    string WorkspaceName,
    string Name,
    LifecycleState State,
    JsonObject Metadata,
    JsonObject Configuration,
    DateTimeOffset CreatedAt);