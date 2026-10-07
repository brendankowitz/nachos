using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Per-message configuration.</summary>
public sealed record MessageConfiguration(
    [property: JsonPropertyName("reasoning")] ReasoningConfiguration? Reasoning = null);