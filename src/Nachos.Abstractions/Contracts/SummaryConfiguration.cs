using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Summary settings. A null member means "inherit".</summary>
public sealed record SummaryConfiguration(
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("messages_per_short_summary")] int? MessagesPerShortSummary = null,
    [property: JsonPropertyName("messages_per_long_summary")] int? MessagesPerLongSummary = null,
    [property: JsonPropertyName("custom_instructions")] string? CustomInstructions = null);