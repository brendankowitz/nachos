using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Json;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>
/// The JSON isolation rule, and the only place JSON enters or leaves the store. The store keeps private,
/// <b>canonical</b> copies of what it is given and hands out fresh copies, so no <see cref="JsonObject"/> is ever
/// shared with a caller.
/// </summary>
/// <remarks>
/// Caller JSON is read only through <see cref="StrictJsonData.ToCanonical"/>, the shared strict-JSON-data rule: values
/// outside its allowlist, unpaired surrogates, repeated property names, non-finite numbers and nesting deeper than
/// <see cref="StrictJsonData.DefaultMaxDepth"/> are rejected, and no caller converter, getter or <c>ToString</c> runs.
/// Its result is plain data, which is then stored as parsed JSON text: exactly what a client could have sent, so a
/// <see cref="Guid"/>, a date or a <see cref="char"/> built in C# is stored as its JSON string and a number as its JSON
/// literal, the same as the SQL provider, which stores text.
/// </remarks>
internal static class JsonCopy
{
    /// <summary>A store-owned canonical copy of an input value; null becomes <c>{}</c>.</summary>
    /// <param name="field">The input's name for the error message, such as <c>metadata</c>.</param>
    /// <exception cref="NachosValidationException">The value is not strict JSON data (see <see cref="StrictJsonData"/>).</exception>
    public static JsonObject Own(JsonObject? value, string field) => value is null ? new JsonObject() : Canonical(value, field);

    /// <summary>A store-owned canonical copy of an optional replacement; null stays null (meaning "unchanged").</summary>
    /// <exception cref="NachosValidationException">The value is not strict JSON data (see <see cref="StrictJsonData"/>).</exception>
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
        JsonNode canonical;
        try
        {
            canonical = StrictJsonData.ToCanonical(value)!;
        }
        catch (NachosValidationException ex)
        {
            // The helper's text never echoes a key or value; it names the CLR type of a value outside the allowlist.
            throw new NachosValidationException(
                $"{field} contains a value that is not valid JSON or is nested too deeply. {ex.Detail}", ex);
        }

        // Plain data within the parser's depth limit: writing it runs no caller code and parsing it cannot fail.
        return Reparse(canonical.AsObject());
    }

    /// <summary>A fresh, parentless object parsed from the value's JSON text. Never fails for canonical JSON.</summary>
    private static JsonObject Reparse(JsonObject value) => JsonNode.Parse(value.ToJsonString())!.AsObject();
}
