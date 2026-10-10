using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Json;

namespace Nachos.Core.Validation;

/// <summary>Checks only Unicode; leaves numeric lexemes, duplicates, depth and the original envelope intact.</summary>
internal static class JsonUnicodeValidator
{
    public static void Validate(JsonElement value)
    {
        Stack<JsonElement> pending = new();
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject())
                    {
                        StrictJsonData.ToCanonical(JsonValue.Create(ReadName(property)));
                        pending.Push(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray()) pending.Push(item);
                    break;
                case JsonValueKind.String:
                    StrictJsonData.ToCanonical(JsonValue.Create(current));
                    break;
            }
        }
    }

    private static string ReadName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException error) when (error is not ObjectDisposedException)
        {
            // This catch covers only decoding a property name, never traversal, serialization or caller code.
            throw new NachosValidationException("JSON data contains an invalid Unicode property name.", error);
        }
    }
}
