using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Peer-card settings. A null member means "inherit".</summary>
public sealed record PeerCardConfiguration(
    [property: JsonPropertyName("use")] bool? Use = null,
    [property: JsonPropertyName("create")] bool? Create = null,
    [property: JsonPropertyName("custom_instructions")] string? CustomInstructions = null);