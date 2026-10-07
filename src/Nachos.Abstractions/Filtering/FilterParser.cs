using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nachos.Abstractions.Filtering;

/// <summary>
/// Parses the JSON filter syntax of the list endpoints into a <see cref="FilterNode"/> tree, validating every value
/// against the resource's field types. This is the only place filter JSON is interpreted.
/// </summary>
/// <remarks>
/// <para><b>Top level.</b> An object whose keys are AND-ed together. <c>AND</c>, <c>OR</c> and <c>NOT</c> (upper case)
/// each take an array of filter objects; <c>NOT [c1…cn]</c> is <c>NOT (c1 OR … OR cn)</c>. Keys that are not a field
/// of the resource are ignored (they match everything).</para>
/// <para><b>Field values.</b> A scalar is equality, <c>null</c> is <see cref="FilterOp.IsNull"/>, <c>"*"</c> matches
/// everything, a bare array is <see cref="FilterOp.In"/> (an empty one matches nothing, one holding <c>"*"</c>
/// matches everything, and a <c>null</c> element also admits unset values), and an object holds operators from
/// <c>gt gte lt lte ne in contains icontains</c> that are AND-ed. <c>{"ne": null}</c> is
/// <see cref="FilterOp.NotNull"/>. <c>null</c> is only accepted as a plain value, as the operand of <c>ne</c>, and as
/// an <c>in</c> element; the other operators reject it.</para>
/// <para><b>Types.</b> Text takes strings. <c>token_count</c> takes JSON integers or integer strings.
/// <c>created_at</c> takes ISO-8601 dates or date-times and is normalized to a UTC <see cref="DateTimeOffset"/>; a
/// date-only value is UTC midnight and a value without an offset is taken as UTC. <c>is_active</c> takes real JSON
/// booleans only. Text allows <c>eq ne in contains icontains</c>, numbers and timestamps <c>eq ne in gt gte lt lte</c>,
/// booleans <c>eq ne</c>; every type allows null checks. Anything else is rejected.</para>
/// <para><b>Metadata.</b> A nested object is containment per key, recursing into nested objects. A scalar at a path
/// is equality, <c>null</c> means unset, <c>"*"</c> means the key exists, and a bare array is
/// <see cref="FilterOp.JsonContains"/> (not <c>in</c>; use <c>{"in": [...]}</c> for alternatives). An object whose
/// keys are all operators applies them to that path. <c>{"metadata": {"contains": {...}}}</c> equals the bare object.
/// Any other operator on the whole metadata object is rejected. A metadata key named like an operator
/// (<c>gt gte lt lte ne in contains icontains</c>) is read as the operator, so such keys cannot be filtered.</para>
/// </remarks>
public static partial class FilterParser
{
    private const string Wildcard = "*";

    private static readonly HashSet<string> OperatorKeys =
        ["gt", "gte", "lt", "lte", "ne", "in", "contains", "icontains"];

    /// <summary>Parses <paramref name="filters"/> for <paramref name="kind"/>.</summary>
    /// <returns>The filter, or null when <paramref name="filters"/> is null or an empty object.</returns>
    /// <exception cref="NachosValidationException">The filter is malformed or holds an invalid value.</exception>
    public static FilterNode? Parse(JsonNode? filters, ResourceKind kind)
    {
        if (filters is null)
        {
            return null;
        }

        if (filters is not JsonObject obj)
        {
            throw Invalid("Filters must be a JSON object.");
        }

        return obj.Count == 0 ? null : ParseObject(obj, ResourceFields.For(kind));
    }

    private static FilterNode ParseObject(JsonObject obj, IReadOnlyDictionary<string, FieldDefinition> fields)
    {
        var children = new List<FilterNode>();
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "AND":
                    children.Add(Conjunction(ParseList(key, value, fields)));
                    break;
                case "OR":
                    var alternatives = ParseList(key, value, fields);
                    children.Add(alternatives.Count == 0 ? new FilterNode.MatchNone() : new FilterNode.Or(alternatives));
                    break;
                case "NOT":
                    var excluded = ParseList(key, value, fields);
                    children.Add(excluded.Count == 0 ? new FilterNode.MatchAll() : new FilterNode.Not(excluded));
                    break;
                default:
                    if (fields.TryGetValue(key, out var field))
                    {
                        children.Add(ParseField(key, field, value));
                    }

                    break;
            }
        }

        return Conjunction(children);
    }

    private static List<FilterNode> ParseList(
        string key, JsonNode? value, IReadOnlyDictionary<string, FieldDefinition> fields)
    {
        if (value is not JsonArray array)
        {
            throw Invalid($"'{key}' must be an array of filter objects.");
        }

        var children = new List<FilterNode>(array.Count);
        foreach (var element in array)
        {
            if (element is not JsonObject child)
            {
                throw Invalid($"'{key}' must be an array of filter objects.");
            }

            children.Add(ParseObject(child, fields));
        }

        return children;
    }

    /// <summary>Drops <see cref="FilterNode.MatchAll"/> children and unwraps a single one.</summary>
    private static FilterNode Conjunction(List<FilterNode> children)
    {
        children.RemoveAll(child => child is FilterNode.MatchAll);
        return children.Count switch
        {
            0 => new FilterNode.MatchAll(),
            1 => children[0],
            _ => new FilterNode.And(children),
        };
    }

    // ---------------------------------------------------------------------------------------------- columns

    private static FilterNode ParseField(string wireName, FieldDefinition field, JsonNode? value)
    {
        if (field.Type == FieldType.Metadata)
        {
            return ParseMetadata(value);
        }

        return value switch
        {
            null => new FilterNode.Field(field.Column, FilterOp.IsNull, null),
            JsonArray array => ParseIn(wireName, field, array),
            JsonObject operators => ParseOperators(wireName, field, operators),
            _ when IsWildcard(value) => new FilterNode.MatchAll(),
            _ => new FilterNode.Field(field.Column, FilterOp.Eq, Normalize(wireName, field, value)),
        };
    }

    private static FilterNode ParseOperators(string wireName, FieldDefinition field, JsonObject operators)
    {
        if (operators.Count == 0)
        {
            throw Invalid($"Field '{wireName}' has an empty operator object.");
        }

        var children = new List<FilterNode>(operators.Count);
        foreach (var (key, operand) in operators)
        {
            var op = ToOperator(key) ?? throw Invalid($"Unknown operator '{key}' for field '{wireName}'.");
            if (!IsAllowed(field.Type, op))
            {
                throw Invalid($"Operator '{key}' is not allowed for field '{wireName}'.");
            }

            switch (op)
            {
                case FilterOp.In:
                    children.Add(ParseIn(wireName, field, operand as JsonArray
                        ?? throw Invalid($"Operator 'in' for field '{wireName}' requires an array.")));
                    break;
                case FilterOp.Ne when operand is null:
                    children.Add(new FilterNode.Field(field.Column, FilterOp.NotNull, null));
                    break;
                default:
                    if (operand is null)
                    {
                        throw Invalid($"Operator '{key}' for field '{wireName}' does not accept null.");
                    }

                    children.Add(new FilterNode.Field(field.Column, op, Normalize(wireName, field, operand)));
                    break;
            }
        }

        return Conjunction(children);
    }

    private static FilterNode ParseIn(string wireName, FieldDefinition field, JsonArray array)
    {
        if (!IsAllowed(field.Type, FilterOp.In))
        {
            throw Invalid($"A list is not allowed for field '{wireName}'.");
        }

        if (array.Any(IsWildcard))
        {
            return new FilterNode.MatchAll();
        }

        if (array.Count == 0)
        {
            return new FilterNode.MatchNone();
        }

        var values = new JsonArray();
        var includesNull = false;
        foreach (var element in array)
        {
            if (element is null)
            {
                includesNull = true;
            }
            else
            {
                values.Add(Normalize(wireName, field, element));
            }
        }

        var isNull = new FilterNode.Field(field.Column, FilterOp.IsNull, null);
        if (values.Count == 0)
        {
            return isNull;
        }

        var inList = new FilterNode.Field(field.Column, FilterOp.In, values);
        return includesNull ? new FilterNode.Or([inList, isNull]) : (FilterNode)inList;
    }

    private static FilterOp? ToOperator(string key) => key switch
    {
        "gt" => FilterOp.Gt,
        "gte" => FilterOp.Gte,
        "lt" => FilterOp.Lt,
        "lte" => FilterOp.Lte,
        "ne" => FilterOp.Ne,
        "in" => FilterOp.In,
        "contains" => FilterOp.Contains,
        "icontains" => FilterOp.IContains,
        _ => null,
    };

    private static bool IsAllowed(FieldType type, FilterOp op) => op switch
    {
        FilterOp.Eq or FilterOp.Ne or FilterOp.IsNull or FilterOp.NotNull => true,
        FilterOp.In => type != FieldType.Boolean,
        FilterOp.Contains or FilterOp.IContains => type == FieldType.Text,
        FilterOp.Gt or FilterOp.Gte or FilterOp.Lt or FilterOp.Lte => type is FieldType.Number or FieldType.Timestamp,
        _ => false,
    };

    /// <summary>Validates <paramref name="value"/> against the field's type and returns it in normalized form.</summary>
    private static JsonValue Normalize(string wireName, FieldDefinition field, JsonNode value)
    {
        var kind = value is JsonValue ? value.GetValueKind() : (JsonValueKind?)null;
        switch (field.Type)
        {
            case FieldType.Text when kind == JsonValueKind.String:
                return JsonValue.Create(value.GetValue<string>());

            case FieldType.Number when kind == JsonValueKind.Number && value.AsValue().TryGetValue(out long number):
                return JsonValue.Create(number);

            case FieldType.Number when kind == JsonValueKind.String
                && long.TryParse(value.GetValue<string>(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed):
                return JsonValue.Create(parsed);

            case FieldType.Timestamp when kind == JsonValueKind.String && TryParseTimestamp(value.GetValue<string>(), out var instant):
                return JsonValue.Create(instant);

            case FieldType.Boolean when kind is JsonValueKind.True or JsonValueKind.False:
                return JsonValue.Create(kind == JsonValueKind.True);

            default:
                throw Invalid($"Field '{wireName}' expects {Describe(field.Type)}.");
        }
    }

    private static string Describe(FieldType type) => type switch
    {
        FieldType.Text => "a string",
        FieldType.Number => "an integer or an integer string",
        FieldType.Timestamp => "an ISO-8601 date or date-time string",
        FieldType.Boolean => "a boolean (true or false)",
        _ => "a different value",
    };

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})?)?$")]
    private static partial Regex Iso8601();

    private static bool TryParseTimestamp(string text, out DateTimeOffset utc)
    {
        // The regex rejects the free-form dates DateTimeOffset.TryParse would otherwise accept.
        if (Iso8601().IsMatch(text)
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc))
        {
            return true;
        }

        utc = default;
        return false;
    }

    // ---------------------------------------------------------------------------------------------- metadata

    private static FilterNode ParseMetadata(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject root:
                var children = new List<FilterNode>(root.Count);
                foreach (var (key, operand) in root)
                {
                    if (key == "contains")
                    {
                        children.Add(operand is JsonObject contained
                            ? ParseMetadataObject([], contained)
                            : throw Invalid("Operator 'contains' on metadata requires an object."));
                    }
                    else if (OperatorKeys.Contains(key))
                    {
                        throw Invalid(
                            $"Operator '{key}' cannot be applied to the whole metadata object; filter a metadata key instead.");
                    }
                    else
                    {
                        children.Add(ParseMetadataKey([key], operand));
                    }
                }

                return Conjunction(children);

            case not null when IsWildcard(value):
                return new FilterNode.MatchAll();

            default:
                throw Invalid("Field 'metadata' expects an object.");
        }
    }

    private static FilterNode ParseMetadataObject(IReadOnlyList<string> path, JsonObject obj)
    {
        var children = new List<FilterNode>(obj.Count);
        foreach (var (key, value) in obj)
        {
            children.Add(ParseMetadataKey([.. path, key], value));
        }

        return Conjunction(children);
    }

    private static FilterNode ParseMetadataKey(IReadOnlyList<string> path, JsonNode? value)
    {
        switch (value)
        {
            case null:
                return new FilterNode.MetadataPath(path, FilterOp.IsNull, null);
            case JsonArray array:
                return array.Count == 0
                    ? throw Invalid($"Metadata key '{string.Join('.', path)}' needs a non-empty list.")
                    : new FilterNode.MetadataPath(path, FilterOp.JsonContains, array.DeepClone());
            case JsonObject obj:
                var operatorCount = obj.Count(pair => OperatorKeys.Contains(pair.Key));
                if (operatorCount == 0)
                {
                    return ParseMetadataObject(path, obj);
                }

                return operatorCount == obj.Count
                    ? ParseMetadataOperators(path, obj)
                    : throw Invalid($"Metadata key '{string.Join('.', path)}' mixes operators and nested keys.");
            case JsonValue when IsWildcard(value):
                return new FilterNode.MetadataPath(path, FilterOp.NotNull, null);
            default:
                return new FilterNode.MetadataPath(path, FilterOp.Eq, value.DeepClone());
        }
    }

    private static FilterNode ParseMetadataOperators(IReadOnlyList<string> path, JsonObject operators)
    {
        var children = new List<FilterNode>(operators.Count);
        foreach (var (key, operand) in operators)
        {
            var op = ToOperator(key)!.Value;
            var where = $"Operator '{key}' on metadata key '{string.Join('.', path)}'";
            switch (op)
            {
                case FilterOp.Gt or FilterOp.Gte or FilterOp.Lt or FilterOp.Lte:
                    children.Add(new FilterNode.MetadataPath(
                        path, op, RequireKind(where, operand, "a number or a string", JsonValueKind.Number, JsonValueKind.String)));
                    break;
                case FilterOp.Contains or FilterOp.IContains:
                    children.Add(new FilterNode.MetadataPath(
                        path, op, RequireKind(where, operand, "a string", JsonValueKind.String)));
                    break;
                case FilterOp.Ne when operand is null:
                    children.Add(new FilterNode.MetadataPath(path, FilterOp.NotNull, null));
                    break;
                case FilterOp.Ne:
                    children.Add(new FilterNode.MetadataPath(path, op, RequireScalar(where, operand)));
                    break;
                default:
                    children.Add(ParseMetadataIn(path, where, operand));
                    break;
            }
        }

        return Conjunction(children);
    }

    private static FilterNode ParseMetadataIn(IReadOnlyList<string> path, string where, JsonNode? operand)
    {
        if (operand is not JsonArray array)
        {
            throw Invalid($"{where} requires an array.");
        }

        if (array.Any(IsWildcard))
        {
            return new FilterNode.MetadataPath(path, FilterOp.NotNull, null);
        }

        var alternatives = array
            .Select(element => element is null
                ? new FilterNode.MetadataPath(path, FilterOp.IsNull, null)
                : new FilterNode.MetadataPath(path, FilterOp.Eq, RequireScalar(where, element)))
            .ToList<FilterNode>();
        return alternatives.Count switch
        {
            0 => new FilterNode.MatchNone(),
            1 => alternatives[0],
            _ => new FilterNode.Or(alternatives),
        };
    }

    private static JsonNode RequireScalar(string where, JsonNode? operand) =>
        RequireKind(where, operand, "a string, number or boolean", JsonValueKind.String, JsonValueKind.Number, JsonValueKind.True, JsonValueKind.False);

    private static JsonNode RequireKind(string where, JsonNode? operand, string expected, params JsonValueKind[] kinds) =>
        operand is JsonValue value && Array.IndexOf(kinds, value.GetValueKind()) >= 0
            ? value.DeepClone()
            : throw Invalid($"{where} expects {expected}.");

    // ---------------------------------------------------------------------------------------------- helpers

    private static bool IsWildcard(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == Wildcard;

    private static NachosValidationException Invalid(string detail) => new(detail);
}
