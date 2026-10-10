using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// Domain error body: <c>{"detail":"..."}</c> plus the RFC 9457 <c>type</c>, <c>title</c> and <c>status</c> members.
/// </summary>
public sealed record ErrorResponse(
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("status")] int? Status = null);