using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Nachos.Core.Deriver;

/// <summary>
/// The Deriver's structured-output schema (spec §11.2). It is model output, so every field may be missing or wrong;
/// <see cref="DeriverService"/> validates each fact before using it.
/// </summary>
public sealed record ExtractionResult(
    [property: JsonPropertyName("facts")]
    [property: Description("Explicit facts about the target peer; empty when there are none.")]
    IReadOnlyList<ExtractedFact>? Facts);

/// <summary>One fact as the model stated it.</summary>
public sealed record ExtractedFact(
    [property: JsonPropertyName("content")]
    [property: Description("One atomic, self-contained statement about the target peer.")]
    string? Content,
    [property: JsonPropertyName("source_message_ids")]
    [property: Description("The ids of the target peer's messages that state this fact.")]
    IReadOnlyList<string>? SourceMessageIds);
