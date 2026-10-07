using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Filtering;

/// <summary>Validation of filters built as a <see cref="JsonNode"/> tree before they are turned into JSON text.</summary>
/// <remarks>
/// <see cref="Utf8JsonWriter"/> silently replaces an unpaired surrogate with U+FFFD and offers no strict mode, so a
/// constructed tree is walked once, before serialization, and every string a writer would encode is checked first.
/// Strings held directly by the tree are checked here; strings inside a typed <see cref="JsonValue"/> (an array,
/// dictionary or object wrapped with <c>JsonValue.Create</c>) are checked by serializing the value through
/// converters that inspect each <see cref="string"/>, <see cref="char"/>, dictionary key and nested
/// <see cref="JsonNode"/> before writing it. Values backed by JSON text are left to serialization and the strict
/// re-parse, which reject their invalid escapes and duplicate keys themselves.
/// </remarks>
public static partial class FilterParser
{
    /// <summary>
    /// Serializer options for checking typed values. Their depth limit is one above <see cref="MaxDepth"/> because the
    /// serializer refuses to write even a scalar once the writer holds that many open containers, whereas
    /// <see cref="JsonDocumentOptions.MaxDepth"/> allows scalars inside the deepest container. The validation writer
    /// enforces <see cref="MaxDepth"/> itself.
    /// </summary>
    private static readonly JsonSerializerOptions WellFormedTypedValues = new()
    {
        MaxDepth = MaxDepth + 1,
        Converters = { new WellFormedStringConverter(), new WellFormedCharConverter(), new WellFormedNodeConverterFactory() },
    };

    /// <summary>
    /// Throws when <paramref name="filters"/> holds a string with an unpaired surrogate or nests objects and arrays
    /// deeper than <see cref="MaxDepth"/>, the same container-only depth <see cref="JsonDocumentOptions.MaxDepth"/>
    /// counts for the text overload.
    /// </summary>
    /// <exception cref="NachosValidationException">A string is not well-formed UTF-16.</exception>
    /// <exception cref="InvalidOperationException">The tree is too deep, or a JSON-backed key holds an invalid escape.</exception>
    /// <exception cref="ArgumentException">A JSON-backed object repeats a key.</exception>
    /// <exception cref="JsonException">A typed value cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A typed value cannot be serialized.</exception>
    private static void RequireRepresentable(JsonNode filters)
    {
        // The output is discarded: ToJsonString produces the text the parser reads, so typed values keep their own
        // serialization contract. This writer only tracks depth and drives the typed values' converters.
        using var writer = new Utf8JsonWriter(Stream.Null, new JsonWriterOptions { MaxDepth = MaxDepth });
        WriteWellFormed(writer, filters);
    }

    private static void WriteWellFormed(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();

                // Enumerating a JSON-backed object decodes its keys, which throws on a duplicate or invalid escape.
                foreach (var (key, value) in obj)
                {
                    RequireWellFormed(key);
                    writer.WritePropertyName(key);
                    WriteWellFormed(writer, value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var element in array)
                {
                    WriteWellFormed(writer, element);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value:
                WriteWellFormed(writer, value);
                break;
        }
    }

    private static void WriteWellFormed(Utf8JsonWriter writer, JsonValue value)
    {
        if (value.TryGetValue(out JsonElement _))
        {
            // Backed by JSON text: serialization and the strict re-parse reject its invalid escapes and duplicates.
            writer.WriteNullValue();
        }
        else if (value.TryGetValue(out string? text))
        {
            RequireWellFormed(text);
            writer.WriteNullValue();
        }
        else if (value.TryGetValue(out char character))
        {
            RequireWellFormed(character.ToString());
            writer.WriteNullValue();
        }
        else if (value.TryGetValue(out object? typed))
        {
            JsonSerializer.Serialize(writer, typed, typed.GetType(), WellFormedTypedValues);
        }
        else
        {
            throw new NotSupportedException($"A JSON value of type '{value.GetType()}' cannot be inspected.");
        }
    }

    private sealed class WellFormedStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            RequireWellFormed(value);
            writer.WriteStringValue(value);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            RequireWellFormed(value);
            writer.WritePropertyName(value);
        }
    }

    private sealed class WellFormedCharConverter : JsonConverter<char>
    {
        public override char Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
        {
            RequireWellFormed(value.ToString());
            writer.WriteStringValue(value.ToString());
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
        {
            RequireWellFormed(value.ToString());
            writer.WritePropertyName(value.ToString());
        }
    }

    /// <summary>
    /// A <see cref="JsonNode"/> inside a typed value writes its strings with its own converters, bypassing
    /// <see cref="WellFormedTypedValues"/>, so it is walked like a node of the filter itself.
    /// </summary>
    private sealed class WellFormedNodeConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeof(JsonNode).IsAssignableFrom(typeToConvert);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(WellFormedNodeConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class WellFormedNodeConverter<TNode> : JsonConverter<TNode>
        where TNode : JsonNode
    {
        public override TNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, TNode value, JsonSerializerOptions options) =>
            WriteWellFormed(writer, value);
    }
}
