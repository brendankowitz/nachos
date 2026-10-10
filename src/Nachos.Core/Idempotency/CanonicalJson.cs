using System.Buffers;
using System.Text.Json;

namespace Nachos.Core.Idempotency;

public static class CanonicalJson
{
    /// <summary>
    /// Produces compact UTF-8 JSON with ordinally sorted object keys. Array order, duplicate
    /// properties, unknown fields, explicit nulls and numeric lexemes are retained.
    /// </summary>
    public static byte[] Serialize(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        Write(writer, value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var element in value.EnumerateArray())
                {
                    Write(writer, element);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
