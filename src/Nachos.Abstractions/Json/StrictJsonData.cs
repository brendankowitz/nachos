using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Json;

/// <summary>
/// The single strict-JSON-data boundary for values built in C#: filters, stored metadata and configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> <see cref="Utf8JsonWriter"/> silently rewrites an unpaired surrogate to U+FFFD, and a
/// <see cref="JsonValue"/> can carry caller-supplied converters, serialization metadata and arbitrary CLR objects.
/// Serializing such a tree would either lose data silently or run caller code. <see cref="ToCanonical"/> therefore
/// reads the tree through a closed allowlist and returns a fresh tree that holds nothing but plain JSON data. Providers
/// and in-process clients must pass every constructed metadata or configuration value through it at ingress, rather
/// than serializing the caller's tree themselves, so there is one rule and one implementation.
/// </para>
/// <para>
/// <b>Accepted.</b> <c>null</c>; <see cref="JsonObject"/> and <see cref="JsonArray"/>, walked recursively with each
/// property name checked; and a <see cref="JsonValue"/> backed by a <see cref="JsonElement"/>, <see cref="string"/>,
/// <see cref="char"/>, <see cref="bool"/>, <see cref="sbyte"/>, <see cref="byte"/>, <see cref="short"/>,
/// <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, <see cref="ulong"/>,
/// <see cref="float"/>, <see cref="double"/>, <see cref="decimal"/>, <see cref="DateTime"/>,
/// <see cref="DateTimeOffset"/> or <see cref="Guid"/>. Anything else (collections, dictionaries, POCOs, enums,
/// <see cref="Half"/>, <see cref="Int128"/>, <see cref="TimeSpan"/>, <see cref="DateOnly"/>, a <see cref="JsonNode"/>
/// nested inside a typed value, and so on) is rejected. A value is classified by its backing runtime value, never by
/// the type it was declared as: an interface or base-type projection of a non-allowlisted runtime type is rejected,
/// and one of an allowlisted type (for example an <see cref="int"/> declared as <see cref="IComparable"/>) is
/// accepted. The accepted types are read from the backing value as they are: no getter, converter or <c>ToString</c> of a caller type ever runs, and any
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> or converter attached to a
/// <see cref="JsonValue"/> is ignored.
/// </para>
/// <para>
/// <b>Canonical form.</b> Every object and array of the result is a new <see cref="JsonObject"/> or
/// <see cref="JsonArray"/>. A <see cref="string"/> stays a string and a <see cref="char"/> becomes a one-character
/// string. Integers, floating-point numbers, <see cref="decimal"/> and <see cref="bool"/> keep their value. A
/// <see cref="DateTime"/> or <see cref="DateTimeOffset"/> becomes its ISO 8601 string and a <see cref="Guid"/> its
/// canonical <c>D</c> text, exactly as they would be written to JSON. A JSON-backed number keeps its digits as
/// written (a detached copy) so no precision is lost. The result therefore equals what the same data would be if
/// received as JSON text, and it does not alias the input: changing the input afterwards does not change the result.
/// </para>
/// <para>
/// <b>Depth.</b> Only objects and arrays count, matching <see cref="JsonDocumentOptions.MaxDepth"/>: a root container
/// is depth 1, and scalars and <c>null</c> add no level. With the default of 64, a tree of 64 nested containers is
/// accepted and 65 is rejected. The walk recurses once per level, so <c>maxDepth</c> is itself capped at
/// <see cref="MaxAllowedDepth"/>, and a deeper input is rejected after that many levels instead of exhausting the
/// stack.
/// </para>
/// <para>
/// <b>JSON-backed values.</b> Values that came from parsed JSON (<see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>,
/// <see cref="JsonElement"/>) are decoded here, strings and property names included, and the decoded text is checked
/// for unpaired surrogates, so an escaped lone surrogate such as <c>"\uD800"</c> is rejected whichever way the
/// framework decodes it. A repeated property name in a JSON-backed object is rejected, matching the strict text
/// parser.
/// </para>
/// <para>
/// <b>Limits.</b> The helper validates the data it is given, not where it came from. Data a caller has already
/// converted (for example with <c>JsonSerializer.SerializeToNode</c>) is treated as data, and cannot prove that its
/// earlier CLR source was well formed: an unpaired surrogate the serializer already rewrote to U+FFFD is a valid
/// U+FFFD here.
/// </para>
/// </remarks>
public static class StrictJsonData
{
    /// <summary>The default depth limit: 64 nested objects and arrays, the same as <see cref="JsonDocumentOptions.MaxDepth"/>.</summary>
    public const int DefaultMaxDepth = 64;

    /// <summary>The largest <c>maxDepth</c> accepted, the <see cref="Utf8JsonWriter"/> default. It bounds the recursion.</summary>
    public const int MaxAllowedDepth = 1000;

    /// <summary>
    /// Returns a fresh tree holding the strict JSON data of <paramref name="value"/>. See the type remarks for the
    /// accepted values, the canonical form and the depth rule.
    /// </summary>
    /// <param name="value">The tree to read; it is never modified or referenced by the result.</param>
    /// <param name="maxDepth">The most nested objects and arrays allowed, from 1 to <see cref="MaxAllowedDepth"/>.</param>
    /// <returns>The canonical tree, or <c>null</c> when <paramref name="value"/> is <c>null</c> or JSON <c>null</c>.</returns>
    /// <exception cref="NachosValidationException">
    /// A value is outside the allowlist (the message names its CLR type), a number is not finite, a string or property
    /// name holds an unpaired surrogate or an invalid escape, a JSON-backed object repeats a property name, or the tree
    /// is nested deeper than <paramref name="maxDepth"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDepth"/> is less than 1 or more than <see cref="MaxAllowedDepth"/>.</exception>
    /// <exception cref="ObjectDisposedException">A <see cref="JsonElement"/> in the input belongs to a disposed document (a programming error).</exception>
    public static JsonNode? ToCanonical(JsonNode? value, int maxDepth = DefaultMaxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDepth, MaxAllowedDepth);

        try
        {
            return Canonicalize(value, 0, maxDepth);
        }
        catch (Exception ex) when (ex is (InvalidOperationException or ArgumentException) and not ObjectDisposedException)
        {
            // Decoding a JSON-backed object or string throws these for an invalid escape or a repeated property name.
            // The detail stays in the inner exception: the message never echoes a key or value.
            throw new NachosValidationException("JSON data is not valid.", ex);
        }
    }

    private static JsonNode? Canonicalize(JsonNode? node, int depth, int maxDepth)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                {
                    RequireDepth(depth + 1, maxDepth);
                    var result = new JsonObject();

                    // Enumerating a JSON-backed object decodes its keys, which throws on a duplicate or invalid escape.
                    foreach (var (key, child) in obj)
                    {
                        RequireWellFormed(key);
                        result.Add(key, Canonicalize(child, depth + 1, maxDepth));
                    }

                    return result;
                }

            case JsonArray array:
                {
                    RequireDepth(depth + 1, maxDepth);
                    var result = new JsonArray();
                    foreach (var child in array)
                    {
                        result.Add(Canonicalize(child, depth + 1, maxDepth));
                    }

                    return result;
                }

            default:
                return CanonicalizeValue((JsonValue)node, depth, maxDepth);
        }
    }

    private static JsonNode? CanonicalizeValue(JsonValue value, int depth, int maxDepth)
    {
        // JsonElement first: TryGetValue<T> converts a JSON-backed value, so it would also satisfy the checks below.
        if (value.TryGetValue(out JsonElement element))
        {
            return FromElement(element, depth, maxDepth);
        }

        if (value.TryGetValue(out string? text))
        {
            RequireWellFormed(text);
            return JsonValue.Create(text);
        }

        if (value.TryGetValue(out char character))
        {
            var characterText = character.ToString();
            RequireWellFormed(characterText);
            return JsonValue.Create(characterText);
        }

        if (value.TryGetValue(out bool boolean))
        {
            return JsonValue.Create(boolean);
        }

        if (value.TryGetValue(out sbyte sbyteValue))
        {
            return JsonValue.Create(sbyteValue);
        }

        if (value.TryGetValue(out byte byteValue))
        {
            return JsonValue.Create(byteValue);
        }

        if (value.TryGetValue(out short shortValue))
        {
            return JsonValue.Create(shortValue);
        }

        if (value.TryGetValue(out ushort ushortValue))
        {
            return JsonValue.Create(ushortValue);
        }

        if (value.TryGetValue(out int intValue))
        {
            return JsonValue.Create(intValue);
        }

        if (value.TryGetValue(out uint uintValue))
        {
            return JsonValue.Create(uintValue);
        }

        if (value.TryGetValue(out long longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (value.TryGetValue(out ulong ulongValue))
        {
            return JsonValue.Create(ulongValue);
        }

        if (value.TryGetValue(out float floatValue))
        {
            RequireFinite(floatValue);
            return JsonValue.Create(floatValue);
        }

        if (value.TryGetValue(out double doubleValue))
        {
            RequireFinite(doubleValue);
            return JsonValue.Create(doubleValue);
        }

        if (value.TryGetValue(out decimal decimalValue))
        {
            return JsonValue.Create(decimalValue);
        }

        if (value.TryGetValue(out DateTime dateTime))
        {
            return JsonValue.Create(WrittenAsString(writer => writer.WriteStringValue(dateTime)));
        }

        if (value.TryGetValue(out DateTimeOffset dateTimeOffset))
        {
            return JsonValue.Create(WrittenAsString(writer => writer.WriteStringValue(dateTimeOffset)));
        }

        if (value.TryGetValue(out Guid guid))
        {
            return JsonValue.Create(WrittenAsString(writer => writer.WriteStringValue(guid)));
        }

        throw new NachosValidationException(
            $"The value is backed by '{BackingType(value)}', which is not a JSON data type. Build it with JsonObject "
            + "and JsonArray, or convert it first with JsonSerializer.SerializeToNode.");
    }

    // A JsonValue can hold an object or array element when it was created with a JsonTypeInfo
    // (Create<JsonElement>(element, typeInfo) or Create<object>), so the element is walked like any JSON data.
    private static JsonNode? FromElement(JsonElement element, int depth, int maxDepth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString()!;
                RequireWellFormed(text);
                return JsonValue.Create(text);
            case JsonValueKind.Number:
                // A detached copy keeps the digits as written, whatever their size.
                return JsonValue.Create(element.Clone());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Object:
                {
                    RequireDepth(depth + 1, maxDepth);
                    var result = new JsonObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        var name = property.Name;
                        RequireWellFormed(name);
                        if (result.ContainsKey(name))
                        {
                            throw new NachosValidationException("JSON data repeats a property name.");
                        }

                        result.Add(name, FromElement(property.Value, depth + 1, maxDepth));
                    }

                    return result;
                }

            case JsonValueKind.Array:
                {
                    RequireDepth(depth + 1, maxDepth);
                    var result = new JsonArray();
                    foreach (var child in element.EnumerateArray())
                    {
                        result.Add(FromElement(child, depth + 1, maxDepth));
                    }

                    return result;
                }

            default:
                throw new NachosValidationException("The value is an undefined JSON element.");
        }
    }

    private static void RequireDepth(int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new NachosValidationException($"JSON data is nested more than {maxDepth.ToString(CultureInfo.InvariantCulture)} objects or arrays deep.");
        }
    }

    private static void RequireFinite(double number)
    {
        if (!double.IsFinite(number))
        {
            throw new NachosValidationException("JSON data holds a number that is not finite, so it is not a valid JSON number.");
        }
    }

    private static void RequireWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                throw new NachosValidationException("JSON data contains a string with an unpaired surrogate.");
            }
        }
    }

    // Reading the backing value runs no caller code; only its runtime type is used.
    private static Type BackingType(JsonValue value) =>
        value.TryGetValue(out object? backing) && backing is not null ? backing.GetType() : value.GetType();

    // The framework's own JSON text for a date or Guid: a quoted token that needs no escaping, so the quotes come off.
    private static string WrittenAsString(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan[1..^1]);
    }
}
