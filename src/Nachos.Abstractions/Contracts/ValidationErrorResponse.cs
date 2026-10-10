using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// Request-validation body: <c>{"detail":[{"loc":[...],"msg":"...","type":"..."}]}</c> (HTTPValidationError) plus the
/// RFC 9457 <c>type</c>, <c>title</c> and <c>status</c> members.
/// </summary>
public sealed record ValidationErrorResponse(
    [property: JsonPropertyName("detail")] IReadOnlyList<ValidationError> Detail,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("status")] int? Status = null);