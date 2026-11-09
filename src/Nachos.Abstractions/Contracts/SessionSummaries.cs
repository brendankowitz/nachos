using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A session's current summaries (<c>GET S/summaries</c>); each is null until one has been made.</summary>
public sealed record SessionSummaries(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("short_summary")] Summary? ShortSummary,
    [property: JsonPropertyName("long_summary")] Summary? LongSummary);
