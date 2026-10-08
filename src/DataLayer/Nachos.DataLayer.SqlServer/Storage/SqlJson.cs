using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Json;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// The only place caller JSON enters or leaves this provider. Ingress reads it through
/// <see cref="StrictJsonData.ToCanonical"/> (the shared strict-JSON-data rule) and then fits the canonical tree to what
/// SQL Server's <c>json</c> columns store <b>exactly</b>; egress parses the stored text into fresh, parentless nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Numbers.</b> A <c>json</c> column keeps a number exactly only when it is plain decimal text with at most
/// <see cref="ExactDecimal.MaxPrecision"/> significant positions. Other literals it alters silently (an exponent form
/// goes through <c>float</c> and is rewritten with ten decimals; a longer literal is truncated to ten decimals, so
/// <c>1e-29</c> becomes <c>0</c>) or rejects (error 1007). So a plain literal within the limit is stored as written,
/// any other literal is rewritten to the plain text of the same value (<c>1e2</c> becomes <c>100</c>), and a value that
/// needs more than 38 positions is rejected with <see cref="NachosValidationException"/> instead of being stored altered.
/// <c>SqlJsonFidelityTests</c> pins both the column's behaviour and this rule.
/// </para>
/// <para>
/// <b>Keys.</b> <c>OPENJSON</c>, which filters read metadata through, truncates keys to 4000 UTF-16 code units, so a
/// longer key could never be matched exactly and is rejected.
/// </para>
/// </remarks>
internal static class SqlJson
{
    /// <summary>The longest object key <c>OPENJSON</c> returns whole.</summary>
    public const int MaxKeyLength = 4000;

    /// <summary>The JSON text to store for a value that defaults to <c>{}</c> when null.</summary>
    /// <param name="field">The input's name for the error message, such as <c>metadata</c>.</param>
    /// <exception cref="NachosValidationException">The value is not strict JSON data, or SQL Server cannot store it exactly.</exception>
    public static string ToStorage(JsonObject? value, string field) => value is null ? "{}" : Canonical(value, field);

    /// <summary>The JSON text to store for an optional replacement; null stays null (meaning "unchanged").</summary>
    /// <exception cref="NachosValidationException">The value is not strict JSON data, or SQL Server cannot store it exactly.</exception>
    public static string? ToStorageOptional(JsonObject? value, string field) => value is null ? null : Canonical(value, field);

    /// <summary>A fresh, parentless object parsed from stored JSON text.</summary>
    public static JsonObject FromStorage(string stored) => JsonNode.Parse(stored)!.AsObject();

    private static string Canonical(JsonObject value, string field)
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

        return FitToColumn(canonical, field)!.ToJsonString();
    }

    /// <summary>A copy of the canonical tree with every number in the column's exact domain; throws for what cannot be.</summary>
    private static JsonNode? FitToColumn(JsonNode? node, string field)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                {
                    var result = new JsonObject();
                    foreach (var (key, child) in obj)
                    {
                        if (key.Length > MaxKeyLength)
                        {
                            throw new NachosValidationException(
                                $"{field} has a key longer than {MaxKeyLength} UTF-16 code units, which this provider cannot store.");
                        }

                        result.Add(key, FitToColumn(child, field));
                    }

                    return result;
                }

            case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var child in array)
                    {
                        result.Add(FitToColumn(child, field));
                    }

                    return result;
                }

            default:
                return FitNumber((JsonValue)node, field);
        }
    }

    private static JsonNode FitNumber(JsonValue value, string field)
    {
        if (value.GetValueKind() != JsonValueKind.Number)
        {
            return value.DeepClone();
        }

        var literal = value.ToJsonString();
        if (ExactDecimal.IsStoredVerbatim(literal))
        {
            return value.DeepClone();
        }

        var plain = ExactDecimal.Parse(literal).ToPlain()
            ?? throw new NachosValidationException(
                $"{field} holds a number that needs more than {ExactDecimal.MaxPrecision} significant digits, which this provider cannot store exactly.");
        return JsonNode.Parse(plain)!;
    }
}
