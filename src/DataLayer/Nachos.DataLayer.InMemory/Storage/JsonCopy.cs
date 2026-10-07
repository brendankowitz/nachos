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
/// A C#-built string holding a lone surrogate is written as U+FFFD by System.Text.Json; an escaped lone surrogate in
/// text received from a client is rejected.
/// </remarks>
internal static class JsonCopy
{
    /// <summary>A store-owned canonical copy of an input value; null becomes <c>{}</c>.</summary>
    /// <param name="field">The input's name for the error message, such as <c>metadata</c>.</param>
    /// <exception cref="NachosValidationException">The value has no JSON form (for example NaN or Infinity), repeats a property name, or is nested too deeply.</exception>
    public static JsonObject Own(JsonObject? value, string field) => value is null ? new JsonObject() : Canonical(value, field);

    /// <summary>A store-owned canonical copy of an optional replacement; null stays null (meaning "unchanged").</summary>
    /// <exception cref="NachosValidationException">The value has no JSON form (for example NaN or Infinity), repeats a property name, or is nested too deeply.</exception>
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
            return Reparse(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or JsonException)
        {
            // The message names the field only; the offending value stays out of it.
            throw new NachosValidationException($"{field} contains a value that is not valid JSON or is nested too deeply.", ex);
        }
    }

    private static readonly JsonDocumentOptions StrictDocument = new() { AllowDuplicateProperties = false };

    /// <summary>A fresh, parentless object parsed from the value's JSON text. Never fails for stored (canonical) JSON.</summary>
    private static JsonObject Reparse(JsonObject value) =>
        (JsonObject)JsonNode.Parse(value.ToJsonString(), documentOptions: StrictDocument)!;
}
