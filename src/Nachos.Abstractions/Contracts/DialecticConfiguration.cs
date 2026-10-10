using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Dialectic settings. A null member means "inherit".</summary>
public sealed record DialecticConfiguration(
    [property: JsonPropertyName("custom_instructions")] string? CustomInstructions = null);