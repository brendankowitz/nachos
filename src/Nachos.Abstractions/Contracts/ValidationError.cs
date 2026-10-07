using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>One entry of the <c>422 {"detail":[...]}</c> request-validation body.</summary>
/// <param name="Loc">Path to the offending input: strings for members, integers for array indexes.</param>
public sealed record ValidationError(
    [property: JsonPropertyName("loc")] IReadOnlyList<object> Loc,
    [property: JsonPropertyName("msg")] string Msg,
    [property: JsonPropertyName("type")] string Type);