using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>
/// The JSON isolation rule, and the only place JSON enters or leaves the store. The store keeps private,
/// <b>canonical</b> copies of what it is given and hands out fresh copies, so no <see cref="JsonObject"/> is ever
/// shared with a caller.
/// </summary>
/// <remarks>
/// Canonical means re-parsed from its JSON text. A <see cref="JsonValue"/> built in C# keeps its CLR type (a
/// <see cref="Guid"/>, <see cref="DateTimeOffset"/>, <see cref="char"/>…) through <c>DeepClone</c>, so filters would see
/// a non-string value where a client, and the SQL provider which stores text, see a JSON string. Re-parsing makes stored
/// JSON exactly what a client could have sent. Duplicate property names are rejected: a lazily parsed
/// <see cref="JsonObject"/> keeps them until it is enumerated, and a stored one would make every filtered list throw.
/// <para>
/// A string with an unpaired surrogate is rejected rather than stored: System.Text.Json would silently write it as
/// U+FFFD. Before re-parsing, every property name and every <see cref="string"/> or <see cref="char"/> leaf the tree
/// exposes directly (through <see cref="JsonObject"/> keys and <see cref="JsonValue.TryGetValue{T}"/>) is checked, and
/// JSON text with an escaped unpaired surrogate fails to decode. Limit: a string inside an opaque CLR object or
/// collection wrapped in a <see cref="JsonValue"/> (for example <c>JsonValue.Create(new[] { "\uD800" })</c>) is not
/// inspected and is still written as U+FFFD. This input contract is under review and may change.
/// </para>
/// </remarks>
internal static class JsonCopy
{
    /// <summary>A store-owned canonical copy of an input value; null becomes <c>{}</c>.</summary>
    /// <param name="field">The input's name for the error message, such as <c>metadata</c>.</param>
    /// <exception cref="NachosValidationException">The value has no JSON form (for example NaN, Infinity or a string with an unpaired surrogate), repeats a property name, or is nested too deeply.</exception>
    public static JsonObject Own(JsonObject? value, string field) => value is null ? new JsonObject() : Canonical(value, field);

    /// <summary>A store-owned canonical copy of an optional replacement; null stays null (meaning "unchanged").</summary>
    /// <exception cref="NachosValidationException">The value has no JSON form (for example NaN, Infinity or a string with an unpaired surrogate), repeats a property name, or is nested too deeply.</exception>
    public static JsonObject? OwnOptional(JsonObject? value, string field) => value is null ? null : Canonical(value, field);

    public static WorkspaceRecord Out(WorkspaceRecord record) =>
        record with { Metadata = Reparse(record.Metadata), Configuration = Reparse(record.Configuration) };

    public static PeerRecord Out(PeerRecord record) =>
        record with { Metadata = Reparse(record.Metadata), Configuration = Reparse(record.Configuration) };

    public static SessionRecord Out(SessionRecord record) =>
        record with { Metadata = Reparse(record.Metadata), Configuration = Reparse(record.Configuration) };

    public static MessageRecord Out(MessageRecord record) => record with { Metadata = Reparse(record.Metadata) };

    private static JsonObject Canonical(JsonObject value, string field)
    {
        try
        {
            return HasUnpairedSurrogate(value) ? throw Invalid(field) : Reparse(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or JsonException)
        {
            throw Invalid(field, ex);
        }
    }

    // The message names the field only; the offending value stays out of it.
    private static NachosValidationException Invalid(string field, Exception? innerException = null) =>
        new($"{field} contains a value that is not valid JSON or is nested too deeply.", innerException);

    /// <summary>
    /// True when a property name, or a <see cref="string"/> or <see cref="char"/> leaf, that the tree exposes directly
    /// holds an unpaired surrogate. Walks with an explicit stack, so any depth reaches the re-parse's depth limit
    /// instead of overflowing the call stack.
    /// </summary>
    /// <exception cref="InvalidOperationException">A JSON-backed key or string holds an invalid escape.</exception>
    /// <exception cref="ArgumentException">A JSON-backed object repeats a property name.</exception>
    private static bool HasUnpairedSurrogate(JsonObject root)
    {
        var pending = new Stack<JsonNode?>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        if (!IsWellFormedUtf16(key))
                        {
                            return true;
                        }

                        pending.Push(child);
                    }

                    break;
                case JsonArray array:
                    foreach (var element in array)
                    {
                        pending.Push(element);
                    }

                    break;
                case JsonValue leaf when leaf.TryGetValue<string>(out var text) && !IsWellFormedUtf16(text):
                case JsonValue charLeaf when charLeaf.TryGetValue<char>(out var single) && char.IsSurrogate(single):
                    return true;
            }
        }

        return false;
    }

    private static bool IsWellFormedUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[i + 1]))
            {
                return false;
            }

            i++;
        }

        return true;
    }

    private static readonly JsonDocumentOptions StrictDocument = new() { AllowDuplicateProperties = false };

    /// <summary>A fresh, parentless object parsed from the value's JSON text. Never fails for stored (canonical) JSON.</summary>
    private static JsonObject Reparse(JsonObject value) =>
        (JsonObject)JsonNode.Parse(value.ToJsonString(), documentOptions: StrictDocument)!;
}
