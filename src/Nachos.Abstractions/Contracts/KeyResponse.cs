using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Response of the key-issuing route.</summary>
public sealed record KeyResponse([property: JsonPropertyName("key")] string Key);