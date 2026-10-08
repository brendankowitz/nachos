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
/// <b>Accepted.</b> <c>null</c>; <see cref="JsonObject"/> and <see cref="JsonArray"/>, walked in full with each
/// property name checked; and a <see cref="JsonValue"/> backed by a <see cref="JsonElement"/>, <see cref="string"/>,
/// <see cref="char"/>, <see cref="bool"/>, <see cref="sbyte"/>, <see cref="byte"/>, <see cref="short"/>,
/// <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, <see cref="ulong"/>,
/// <see cref="float"/>, <see cref="double"/>, <see cref="decimal"/>, <see cref="DateTime"/>,
/// <see cref="DateTimeOffset"/> or <see cref="Guid"/>. Anything else (collections, dictionaries, POCOs, enums,
/// <see cref="Half"/>, <see cref="Int128"/>, <see cref="TimeSpan"/>, <see cref="DateOnly"/>, a <see cref="JsonNode"/>
/// nested inside a typed value, and so on) is rejected. A value is classified by its backing runtime value, never by
/// the type it was declared as: an interface or base-type projection of a non-allowlisted runtime type is rejected,
/// and one of an allowlisted type (for example an <see cref="int"/> declared as <see cref="IComparable"/>) is
/// accepted. The accepted types are read from the backing value as they are: no getter, converter or
/// <c>ToString</c> of a caller type ever runs, and any
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
/// accepted and 65 is rejected. <c>maxDepth</c> is capped at <see cref="MaxAllowedDepth"/> so the result stays within
/// what <see cref="Utf8JsonWriter"/> accepts by default, and an input nested deeper than <c>maxDepth</c> is rejected
/// as soon as the first container beyond it is reached.
/// </para>
/// <para>
/// <b>Stack safety.</b> <see cref="ToCanonical"/> uses no native recursion of its own: it walks with an explicit
/// stack kept on the heap, for explicit containers and for object and array <see cref="JsonElement"/>s alike, so its
/// frames are constant whatever the depth, including for a very deep input that is then rejected. One framework
/// exception applies: a lazily parsed tree (<see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
/// with null <see cref="JsonNodeOptions"/>) makes System.Text.Json itself recurse once per level as its nodes are first
/// read, about 64 KiB of stack at <see cref="MaxAllowedDepth"/>, which was verified to fit on a 128 KiB thread. Parsing
/// with a non-null <see cref="JsonNodeOptions"/> avoids it. Later System.Text.Json operations on a deep result
/// (<c>ToJsonString</c>, <c>DeepClone</c>, serialization, <c>JsonNode.DeepEquals</c>) also use the framework's own
/// recursion. Callers that handle untrusted depth should keep <c>maxDepth</c> low; <see cref="DefaultMaxDepth"/> is
/// safe for all of them.
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

    /// <summary>The largest <c>maxDepth</c> accepted: the <see cref="Utf8JsonWriter"/> default depth, so a result can still be written.</summary>
    public const int MaxAllowedDepth = 1000;

    private const string NotValid = "JSON data is not valid.";

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
    public static JsonNode? ToCanonical(JsonNode? value, int maxDepth = DefaultMaxDepth) =>
        ToCanonicalCore(value, maxDepth, keepInstants: false);

    /// <summary>
    /// The same, for the operand of a filter node, which providers read back with <c>GetValue&lt;DateTimeOffset&gt;()</c>:
    /// a <see cref="DateTimeOffset"/> stays a <see cref="DateTimeOffset"/> instead of becoming its ISO string. Every
    /// other rule, the default depth limit included, is unchanged.
    /// </summary>
    internal static JsonNode? ToCanonicalOperand(JsonNode? value) =>
        ToCanonicalCore(value, DefaultMaxDepth, keepInstants: true);

    private static JsonNode? ToCanonicalCore(JsonNode? value, int maxDepth, bool keepInstants)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDepth, MaxAllowedDepth);

        try
        {
            return Canonicalize(value, maxDepth, keepInstants);
        }
        catch (Exception ex) when (ex is (InvalidOperationException or ArgumentException) and not ObjectDisposedException)
        {
            // Decoding a JSON-backed object or string throws these for an invalid escape or a repeated property name.
            // The detail stays in the inner exception: the message never echoes a key or value.
            throw new NachosValidationException(NotValid, ex);
        }
    }

    /// <summary>One child of a container being read: a property name (objects only) and a node or an element.</summary>
    private readonly record struct Item(string? Key, JsonNode? Node, JsonElement Element, bool IsElement);

    /// <summary>A container being built, with the children still to read and the name the child in progress goes under.</summary>
    private sealed class Frame(JsonNode result, IEnumerator<Item> children)
    {
        public JsonNode Result { get; } = result;

        public IEnumerator<Item> Children { get; } = children;

        public string? PendingKey { get; set; }

        public void Attach(JsonNode? child)
        {
            if (Result is JsonObject obj)
            {
                obj.Add(PendingKey!, child);
            }
            else
            {
                ((JsonArray)Result).Add(child);
            }
        }
    }

    // Iterative on purpose: the open containers live in this stack, not on the native one, so depth costs heap only.
    private static JsonNode? Canonicalize(JsonNode? root, int maxDepth, bool keepInstants)
    {
        // Created on the first container, so a scalar or null root costs no stack.
        Stack<Frame>? open = null;
        try
        {
            if (TryRead(new Item(null, root, default, false), ref open, maxDepth, keepInstants, out var rootLeaf))
            {
                return rootLeaf;
            }

            while (true)
            {
                var frame = open!.Peek();
                if (frame.Children.MoveNext())
                {
                    var child = frame.Children.Current;
                    if (child.Key is not null)
                    {
                        RequireWellFormed(child.Key);
                        if (((JsonObject)frame.Result).ContainsKey(child.Key))
                        {
                            // Reached for element-backed objects, which a JsonDocument allows to repeat a name. A constructed
                            // JsonObject cannot repeat one and a parsed one fails on enumeration first, so for those this is defensive.
                            throw new NachosValidationException(NotValid, new ArgumentException("A property name is repeated."));
                        }
                    }

                    frame.PendingKey = child.Key;
                    if (TryRead(child, ref open, maxDepth, keepInstants, out var leaf))
                    {
                        frame.Attach(leaf);
                    }

                    continue;
                }

                open.Pop();
                frame.Children.Dispose();
                if (open.Count == 0)
                {
                    return frame.Result;
                }

                open.Peek().Attach(frame.Result);
            }
        }
        finally
        {
            if (open is not null)
            {
                foreach (var frame in open)
                {
                    frame.Children.Dispose();
                }
            }
        }
    }

    // Reads a scalar or null into `leaf` and returns true; for an object or array opens a frame and returns false.
    private static bool TryRead(Item item, ref Stack<Frame>? open, int maxDepth, bool keepInstants, out JsonNode? leaf)
    {
        leaf = null;
        if (item.IsElement)
        {
            return TryReadElement(item.Element, ref open, maxDepth, out leaf);
        }

        switch (item.Node)
        {
            case null:
                return true;
            case JsonObject obj:
                Open(ref open, maxDepth, new JsonObject(), ObjectChildren(obj));
                return false;
            case JsonArray array:
                Open(ref open, maxDepth, new JsonArray(), ArrayChildren(array));
                return false;
            default:
                var value = (JsonValue)item.Node;

                // JsonElement first: TryGetValue<T> converts a JSON-backed value, so it would also satisfy the checks below.
                if (value.TryGetValue(out JsonElement element))
                {
                    return TryReadElement(element, ref open, maxDepth, out leaf);
                }

                leaf = CanonicalizeScalar(value, keepInstants);
                return true;
        }
    }

    // A JsonValue can hold an object or array element when it was created with a JsonTypeInfo
    // (Create<JsonElement>(element, typeInfo) or Create<object>), so the element is read like any JSON data.
    private static bool TryReadElement(JsonElement element, ref Stack<Frame>? open, int maxDepth, out JsonNode? leaf)
    {
        leaf = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString()!;
                RequireWellFormed(text);
                leaf = JsonValue.Create(text);
                return true;
            case JsonValueKind.Number:
                // A detached copy keeps the digits as written, whatever their size.
                leaf = JsonValue.Create(element.Clone());
                return true;
            case JsonValueKind.True:
                leaf = JsonValue.Create(true);
                return true;
            case JsonValueKind.False:
                leaf = JsonValue.Create(false);
                return true;
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Object:
                Open(ref open, maxDepth, new JsonObject(), ElementProperties(element));
                return false;
            case JsonValueKind.Array:
                Open(ref open, maxDepth, new JsonArray(), ElementItems(element));
                return false;
            default:
                throw new NachosValidationException("The value is an undefined JSON element.");
        }
    }

    // Constructed and element levels share the one stack, so they count toward the same limit.
    private static void Open(ref Stack<Frame>? open, int maxDepth, JsonNode result, IEnumerable<Item> children)
    {
        open ??= new Stack<Frame>();
        RequireDepth(open.Count + 1, maxDepth);
        open.Push(new Frame(result, children.GetEnumerator()));
    }

    // Enumerating a JSON-backed object decodes its keys, which throws on a duplicate or invalid escape.
    private static IEnumerable<Item> ObjectChildren(JsonObject obj)
    {
        foreach (var (key, child) in obj)
        {
            yield return new Item(key, child, default, false);
        }
    }

    private static IEnumerable<Item> ArrayChildren(JsonArray array)
    {
        foreach (var child in array)
        {
            yield return new Item(null, child, default, false);
        }
    }

    private static IEnumerable<Item> ElementProperties(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            yield return new Item(property.Name, null, property.Value, true);
        }
    }

    private static IEnumerable<Item> ElementItems(JsonElement element)
    {
        foreach (var child in element.EnumerateArray())
        {
            yield return new Item(null, null, child, true);
        }
    }

    // The scalar allowlist, in the order its checks must run. A JsonElement-backed value never reaches here.
    private static JsonValue? CanonicalizeScalar(JsonValue value, bool keepInstants)
    {
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
            return keepInstants
                ? JsonValue.Create(dateTimeOffset)
                : JsonValue.Create(WrittenAsString(writer => writer.WriteStringValue(dateTimeOffset)));
        }

        if (value.TryGetValue(out Guid guid))
        {
            return JsonValue.Create(WrittenAsString(writer => writer.WriteStringValue(guid)));
        }

        throw new NachosValidationException(
            $"The value is backed by '{BackingType(value)}', which is not a JSON data type. Build it with JsonObject "
            + "and JsonArray, or convert it first with JsonSerializer.SerializeToNode.");
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

    /// <summary>Throws when <paramref name="text"/> holds an unpaired surrogate.</summary>
    /// <exception cref="NachosValidationException">The text is not well-formed UTF-16.</exception>
    internal static void RequireWellFormed(string text)
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
