using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Dream settings. A null member means "inherit".</summary>
public sealed record DreamConfiguration(
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("custom_instructions")] string? CustomInstructions = null);