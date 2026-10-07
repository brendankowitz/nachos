using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Workspace-level configuration. A null member means "inherit" (workspace falls back to defaults).</summary>
public sealed record WorkspaceConfiguration(
    [property: JsonPropertyName("reasoning")] ReasoningConfiguration? Reasoning = null,
    [property: JsonPropertyName("peer_card")] PeerCardConfiguration? PeerCard = null,
    [property: JsonPropertyName("summary")] SummaryConfiguration? Summary = null,
    [property: JsonPropertyName("dream")] DreamConfiguration? Dream = null,
    [property: JsonPropertyName("dialectic")] DialecticConfiguration? Dialectic = null,
    [property: JsonPropertyName("custom_instructions")] string? CustomInstructions = null);