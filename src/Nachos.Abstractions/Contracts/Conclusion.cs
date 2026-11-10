using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A conclusion as exposed on the wire.</summary>
/// <param name="SessionId">The session an explicit conclusion was stated in; null when it is not session-attributed.</param>
/// <param name="Level">One of <c>explicit</c>, <c>deductive</c>, <c>inductive</c> or <c>contradiction</c>.</param>
/// <param name="SourceIds">The ids of the conclusions this one was reasoned from; empty for explicit conclusions.</param>
/// <param name="SourceMessageIds">
/// The ids of the messages the deriver cited for an explicit conclusion, in message order; null for derived levels and
/// for conclusions created through the API.
/// </param>
public sealed record Conclusion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("observer_id")] string ObserverId,
    [property: JsonPropertyName("observed_id")] string ObservedId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("times_derived")] int TimesDerived,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("source_message_ids")] IReadOnlyList<string>? SourceMessageIds = null);
