using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nachos.Client;

/// <summary>Parses response bodies strictly: duplicate property names are invalid JSON here.</summary>
/// <remarks>
/// A lenient parse would surface a duplicate key later as an <see cref="ArgumentException"/> when the node is
/// read, which is neither a protocol error nor the caller's fault.
/// </remarks>
internal static class WireJson
{
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <exception cref="JsonException">The text is not valid JSON or repeats a property name.</exception>
    public static JsonNode? Parse(string json) => JsonNode.Parse(json, documentOptions: Strict);
}
