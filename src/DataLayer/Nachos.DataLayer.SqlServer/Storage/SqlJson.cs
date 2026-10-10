using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Json;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// The only place caller JSON enters or leaves this provider. Ingress reads it through
/// <see cref="StrictJsonData.ToCanonical"/> (the shared strict-JSON-data rule) and stores the canonical text exactly as
/// that helper emits it; egress parses the stored text into fresh, parentless nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lossless.</b> The columns are <c>nvarchar(max)</c> with a CHECK that the text is a JSON object, so nothing is
/// rewritten: number text is kept byte for byte (<c>1e2</c> stays <c>1e2</c>, <c>1E400</c> and <c>5E-324</c> stay as
/// written, and a 40-digit integer keeps every digit). There is no number normalization and no size or precision cap.
/// Filters compare numbers by exact value whatever their spelling (see <c>dbo.JsonNumberOrderKey</c>).
/// </para>
/// <para>
/// <b>Inherent SQL Server limit.</b> <c>OPENJSON</c>, which filters read metadata through, returns object keys as
/// <c>nvarchar(4000)</c> and truncates longer ones, so such a key could never be matched exactly. A <b>metadata</b> key
/// longer than <see cref="MaxKeyLength"/> UTF-16 code units is therefore rejected with
/// <see cref="NachosValidationException"/> (<see cref="KeyTooLong"/>). Configuration is never filtered, so its keys are
/// not limited.
/// </para>
/// </remarks>
internal static class SqlJson
{
    /// <summary>The longest object key <c>OPENJSON</c> returns whole.</summary>
    public const int MaxKeyLength = 4000;

    /// <summary>The fixed detail of the 422 for an over-long key; it never echoes the key.</summary>
    public const string KeyTooLong =
        "metadata has an object key longer than 4000 UTF-16 code units, which the SQL Server provider cannot store.";

    /// <summary>The JSON text to store for a value that defaults to <c>{}</c> when null.</summary>
    /// <param name="field">Which column the value is for: it names the input in errors and decides the key limit.</param>
    /// <exception cref="NachosValidationException">The value is not strict JSON data, or is metadata with an over-long key.</exception>
    public static string ToStorage(JsonObject? value, JsonField field) => value is null ? "{}" : Canonical(value, field);

    /// <summary>The JSON text to store for an optional replacement; null stays null (meaning "unchanged").</summary>
    /// <exception cref="NachosValidationException">The value is not strict JSON data, or is metadata with an over-long key.</exception>
    public static string? ToStorageOptional(JsonObject? value, JsonField field) => value is null ? null : Canonical(value, field);

    /// <summary>A fresh, parentless object parsed from stored JSON text.</summary>
    public static JsonObject FromStorage(string stored) => JsonNode.Parse(stored)!.AsObject();

    private static string Canonical(JsonObject value, JsonField field)
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
                $"{(field == JsonField.Metadata ? "metadata" : "configuration")} contains a value that is not valid JSON or is nested too deeply. {ex.Detail}", ex);
        }

        if (field == JsonField.Metadata)
        {
            RequireStorableKeys(canonical);
        }

        return canonical.ToJsonString(StorageText);
    }

    /// <summary>
    /// The writer for stored text: the column is data, never HTML, so non-ASCII text is written as itself rather than as
    /// <c>\uXXXX</c> escapes (3 to 6 times larger). Parsing it back yields the same values; quotes, backslashes and control
    /// characters are still escaped, and the strict-data helper has already ruled out unpaired surrogates.
    /// </summary>
    private static readonly JsonSerializerOptions StorageText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Walks the canonical tree (its depth is bounded by the helper) and rejects an over-long key.</summary>
    private static void RequireStorableKeys(JsonNode node)
    {
        var pending = new Stack<JsonNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        if (key.Length > MaxKeyLength)
                        {
                            throw new NachosValidationException(KeyTooLong);
                        }

                        if (child is not null)
                        {
                            pending.Push(child);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var child in array)
                    {
                        if (child is not null)
                        {
                            pending.Push(child);
                        }
                    }

                    break;
            }
        }
    }
}

/// <summary>The JSON columns a caller writes: only metadata is read by filters.</summary>
internal enum JsonField
{
    Metadata,
    Configuration,
}
