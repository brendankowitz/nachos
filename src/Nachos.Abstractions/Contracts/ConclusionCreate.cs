using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// One entry of a conclusion batch create request: an explicit conclusion <paramref name="ObserverId"/> holds about
/// <paramref name="ObservedId"/>. <paramref name="Content"/> has 1 to 65,535 characters.
/// </summary>
public sealed record ConclusionCreate(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("observer_id")] string ObserverId,
    [property: JsonPropertyName("observed_id")] string ObservedId,
    [property: JsonPropertyName("session_id")] string? SessionId = null);
