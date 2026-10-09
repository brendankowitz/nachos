using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Json;

namespace Nachos.Api.Json;

internal sealed class RequestBody(JsonDocument document) : IDisposable
{
    public JsonElement Root => document.RootElement;

    public static async Task<RequestBody> ReadAsync(HttpRequest request, bool requireObject = true)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
        // JsonDocument retains this managed buffer after the stream is disposed.
        ReadOnlyMemory<byte> utf8 = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        if (!Utf8.IsValid(utf8.Span))
        {
            throw Invalid(["body"], "Invalid JSON body.", "json_invalid");
        }
        // Stream parsing accepted a leading BOM; the memory overload does not.
        if (utf8.Span.StartsWith("\uFEFF"u8))
        {
            utf8 = utf8[3..];
        }
        JsonDocument document;
        try
        {
            // Core owns the per-value depth limits; the HTTP envelope must not consume that allowance.
            document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = int.MaxValue });
        }
        catch (JsonException error)
        {
            throw new RequestValidationException([new(["body"], "Invalid JSON body.", "json_invalid")], error);
        }
        if (requireObject && document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw Invalid(["body"], "Body must be an object.", "model_type");
        }
        if (requireObject)
        {
            try
            {
                // Property names decode lazily; validate every root name before order-dependent lookups.
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    _ = ReadPropertyName(property, ["body"]);
                }
            }
            catch (RequestValidationException)
            {
                document.Dispose();
                throw;
            }
        }
        return new(document);
    }

    public string RequiredId()
    {
        if (!Root.TryGetProperty("id", out var value))
        {
            throw Invalid(["body", "id"], "Field required.", "missing");
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(["body", "id"], "Input should be a string.", "string_type");
        }
        var id = ReadValue(value, NachosJsonContext.Default.String, ["body", "id"]);
        if (id.Length == 0)
        {
            throw Invalid(["body", "id"], "String must have at least 1 character.", "string_too_short");
        }
        // JSON Schema counts Unicode code points, not UTF-16 code units.
        var length = 0;
        foreach (var _ in id.EnumerateRunes())
        {
            if (++length > 512)
            {
                throw Invalid(["body", "id"], "String must have at most 512 characters.", "string_too_long");
            }
        }
        foreach (var character in id)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
            {
                throw Invalid(["body", "id"], "String must contain only ASCII letters, digits, '_' or '-'.",
                    "string_pattern_mismatch");
            }
        }
        return id;
    }

    public JsonObject? Object(string property) => Optional(property, NachosJsonContext.Default.JsonObject);

    public T? Optional<T>(string property, JsonTypeInfo<T> typeInfo, bool strict = false) where T : class
    {
        if (!Root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (strict)
        {
            // Check JSON-backed strings before typed deserialization can replace malformed Unicode.
            _ = StrictJsonData.ToCanonical(JsonNode.Parse(value.GetRawText(), new JsonNodeOptions(),
                new JsonDocumentOptions { MaxDepth = int.MaxValue }));
        }
        return ReadValue(value, typeInfo, ["body", property]);
    }

    public Dictionary<string, SessionPeerConfig>? Peers(string? property = null)
    {
        var value = Root;
        object[] location = ["body"];
        if (property is not null)
        {
            if (!Root.TryGetProperty(property, out value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            location = ["body", property];
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(location, "Peers must be an object.", "dict_type");
        }
        Dictionary<string, SessionPeerConfig> peers = new(StringComparer.Ordinal);
        foreach (var peer in value.EnumerateObject())
        {
            var name = ReadPropertyName(peer, location);
            if (peer.Value.ValueKind != JsonValueKind.Object)
            {
                throw Invalid([.. location, name], "Peer configuration must be an object.", "model_type");
            }
            var config = ReadValue(peer.Value, NachosJsonContext.Default.SessionPeerConfig, [.. location, name]);
            if (!peers.TryAdd(name, config))
            {
                throw Invalid([.. location, name], "Duplicate peer.", "value_error");
            }
        }
        return peers;
    }

    public T As<T>(JsonTypeInfo<T> typeInfo) => ReadValue(Root, typeInfo, ["body"]);

    public string[] Strings() => ReadStrings(Root, ["body"]);

    public string[]? OptionalStrings(string property) =>
        !Root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null
            ? null : ReadStrings(value, ["body", property]);

    private static string[] ReadStrings(JsonElement value, object[] location)
    {
        var values = ReadValue(value, NachosJsonContext.Default.StringArray, location);
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is null)
            {
                throw Invalid([.. location, index], "Input should be a string.", "string_type");
            }
        }
        return values;
    }

    public static RequestValidationException Invalid(object[] location, string message, string type) =>
        new([new ValidationError(location, message, type)]);

    private static string ReadPropertyName(JsonProperty property, object[] location)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException error)
        {
            throw new RequestValidationException([new(location, "Invalid JSON property name.", "json_invalid")], error);
        }
    }

    private static T ReadValue<T>(JsonElement value, JsonTypeInfo<T> typeInfo, object[] location)
    {
        try
        {
            return value.Deserialize(typeInfo) ?? throw Invalid(location, "Value must not be null.", "value_error");
        }
        catch (JsonException error)
        {
            throw new RequestValidationException(
                [new ValidationError([.. location, .. PathSegments(error.Path)], "Input has the wrong type.", "value_error")], error);
        }
    }

    private static IEnumerable<object> PathSegments(string? path)
    {
        if (path is null)
        {
            yield break;
        }
        foreach (var segment in path.TrimStart('$', '.').Split(['.', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            yield return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                ? index : segment;
        }
    }

    public void Dispose() => document.Dispose();
}
