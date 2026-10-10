using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Session-level configuration; same shape as <see cref="WorkspaceConfiguration"/>.</summary>
public sealed record SessionConfiguration(
    [property: JsonPropertyName("reasoning")] ReasoningConfiguration? Reasoning = null,
    [property: JsonPropertyName("peer_card")] PeerCardConfiguration? PeerCard = null,
    [property: JsonPropertyName("summary")] SummaryConfiguration? Summary = null,
    [property: JsonPropertyName("dream")] DreamConfiguration? Dream = null,
    [property: JsonPropertyName("dialectic")] DialecticConfiguration? Dialectic = null,
    [property: JsonPropertyName("custom_instructions")] string? CustomInstructions = null);