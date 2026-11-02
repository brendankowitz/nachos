using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A working representation: Markdown sections of conclusions by level, empty when there are none.</summary>
public sealed record RepresentationResponse(
    [property: JsonPropertyName("representation")] string Representation);
