using System.Text;
using System.Text.Json;

namespace Nachos.Core.Validation;

internal static class MessageJsonErrorLocation
{
    /// <summary>
    /// Recovers a structured location without changing admission or the original cause.
    /// For caller-parsed documents retaining comments, a rejected comment immediately after
    /// a field value can be attributed to that field rather than its containing object.
    /// This issue-8 limitation does not relax normal strict-JSON schema-location requirements.
    /// </summary>
    public static List<object> Find(JsonElement messages, JsonException error)
    {
        var location = new List<object> { "body", "messages" };
        // Deserialize(JsonElement) reads these original UTF-8 bytes. Its display Path cannot
        // distinguish arbitrary literal names, but its line/byte coordinates are unambiguous.
        var json = Encoding.UTF8.GetBytes(messages.GetRawText());
        var position = ByteOffset(json, error);
        if (position is null) return location;

        // Recover over every lexical form a JsonElement can preserve. These options do not
        // change DTO admission, which already failed, or add another depth admission rule.
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            MaxDepth = int.MaxValue,
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        reader.Read();
        var index = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var end = reader;
            end.Skip();
            if (position < end.BytesConsumed)
            {
                location.Add(index);
                var field = FindField(ref reader, position.Value, location);
                if (field == "configuration" &&
                    FindField(ref reader, position.Value, location) == "reasoning")
                {
                    FindField(ref reader, position.Value, location);
                }
                return location;
            }
            reader = end;
            index++;
        }
        return location;
    }

    private static string? FindField(ref Utf8JsonReader reader, long position, List<object> location)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            // A rejected comment before a property belongs to the container, not that field.
            if (position < reader.TokenStartIndex) return null;
            var name = reader.GetString()!;
            reader.Read();
            var end = reader;
            end.Skip();
            if (position <= end.BytesConsumed)
            {
                location.Add(name);
                // Metadata and unknown fields are read/skipped as whole values by the DTO
                // deserializer. Only configuration and reasoning have nested DTO boundaries.
                return name;
            }
            reader = end;
        }
        return null;
    }

    private static long? ByteOffset(ReadOnlySpan<byte> json, JsonException error)
    {
        if (error.LineNumber is not { } line || error.BytePositionInLine is not { } column ||
            line < 0 || column < 0) return null;
        var start = 0;
        for (long current = 0; current < line; current++)
        {
            var newline = json[start..].IndexOf((byte)'\n');
            if (newline < 0) return null;
            start += newline + 1;
        }
        return column <= json.Length - start ? start + column : null;
    }
}
