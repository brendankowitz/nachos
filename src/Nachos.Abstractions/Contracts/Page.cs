using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// The pagination envelope. <paramref name="PageNumber"/> is named so because a record member cannot share its
/// enclosing type's name (CS0542); the wire field is still <c>page</c>.
/// </summary>
public sealed record Page<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("page")] int PageNumber,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("pages")] int Pages);