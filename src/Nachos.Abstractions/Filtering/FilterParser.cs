using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nachos.Abstractions.Json;

namespace Nachos.Abstractions.Filtering;

/// <summary>
/// Parses the JSON filter syntax of the list endpoints into a <see cref="FilterNode"/> tree, validating every value
/// against the resource's field types. This is the only place filter JSON is interpreted.
/// </summary>
/// <remarks>
/// <para><b>Input.</b> The <see cref="JsonNode"/> overload first writes the filter as canonical JSON text from strict
/// JSON data only (see <see cref="Parse(JsonNode?, ResourceKind)"/>), so filters built in C# (<c>int</c>,
/// <see cref="DateTimeOffset"/>, <see cref="Guid"/> values and so on) behave exactly like the same filter received
/// over HTTP, and the tree the parser walks is a private copy. Duplicate property names, strings that are not
/// well-formed UTF-16, objects and arrays nested more than 64 deep (scalars add no level, in either overload) and
/// malformed JSON are rejected with a <see cref="NachosValidationException"/>. The same rule governs stored metadata and
/// configuration; see <see cref="StrictJsonData"/>.</para>
/// <para><b>Top level.</b> An object whose keys are AND-ed together. <c>AND</c>, <c>OR</c> and <c>NOT</c> (upper case)
/// each take an array of filter objects; <c>NOT [c1…cn]</c> is <c>NOT (c1 OR … OR cn)</c>. Keys that are not a field
/// of the resource are ignored (they match everything).</para>
/// <para><b>Field values.</b> A scalar is equality, <c>null</c> is <see cref="FilterOp.IsNull"/>, <c>"*"</c> matches
/// everything, a bare array is <see cref="FilterOp.In"/> (an empty one matches nothing, one holding <c>"*"</c>
/// matches everything, and a <c>null</c> element also admits unset values), and an object holds operators from
/// <c>gt gte lt lte ne in contains icontains</c> that are AND-ed. <c>{"ne": null}</c> is
/// <see cref="FilterOp.NotNull"/>. <c>null</c> is only accepted as a plain value, as the operand of <c>ne</c>, and as
/// an <c>in</c> element; the other operators reject it. The operand of <c>ne</c> is a literal even when it is
/// <c>"*"</c>. Lists (<c>in</c>, bare lists, metadata containment lists) hold at most <see cref="MaxListItems"/>
/// elements, and a filter holds at most <see cref="MaxLeaves"/> conditions.</para>
/// <para><b>Types.</b> Text takes strings. <c>token_count</c> takes any JSON number with an integral value
/// (<c>5</c>, <c>5.0</c>, <c>1e2</c>, integers of any size) or an integer string, and is normalized to a
/// <see cref="decimal"/>; a non-integral number is rejected. A number beyond the <see cref="decimal"/> range can never
/// equal a stored count, so <c>eq</c> matches nothing, <c>ne</c> everything, and <c>gt</c>/<c>gte</c>/<c>lt</c>/<c>lte</c>
/// resolve to match-all or match-none by its sign; the parser folds that in, so the tree never holds such a value.
/// <c>created_at</c> takes ISO-8601 dates or date-times and is normalized to a UTC <see cref="DateTimeOffset"/>; a
/// date-only value is UTC midnight and a value without an offset is taken as UTC. <c>is_active</c> takes real JSON
/// booleans only. Text allows <c>eq ne in contains icontains</c>, numbers and timestamps <c>eq ne in gt gte lt lte</c>,
/// booleans <c>eq ne</c>; every type allows null checks. Anything else is rejected.</para>
/// <para><b>Metadata.</b> A nested object is containment per key, recursing into nested objects. A scalar at a path
/// is equality, <c>null</c> means unset, <c>"*"</c> means the key exists with any non-null value (including an object
/// or array), and a bare array is <see cref="FilterOp.JsonContains"/> (not <c>in</c>; use <c>{"in": [...]}</c> for
/// alternatives) whose elements must all be scalars. An object whose keys are all operators applies them to that
/// path; an empty object is rejected. <c>{"metadata": {"contains": {...}}}</c> equals the bare object. Any other
/// operator on the whole metadata object is rejected, and so is a non-object <c>metadata</c> value, except that
/// <c>"*"</c> matches everything and <c>null</c> matches nothing (the column is never unset). A metadata key named
/// like an operator (<c>gt gte lt lte ne in contains icontains</c>) is read as the operator, so such keys cannot be
/// filtered. Keys otherwise match literally, including keys that contain dots, quotes or brackets.</para>
/// </remarks>
public static partial class FilterParser
{
    /// <summary>The most elements an <c>in</c> list, bare list or containment list may hold.</summary>
    /// <remarks>
    /// The cap is <b>per list</b>, and a filter may hold many lists. A SQL provider must therefore pass each list as
    /// one JSON or table-valued parameter rather than one parameter per element, or it can exceed SQL Server's
    /// 2,100-parameter limit.
    /// </remarks>
    public const int MaxListItems = 1000;

    /// <summary>The most conditions (leaves) one filter may hold, wherever they are; more is a validation error.</summary>
    /// <remarks>
    /// Counted on the filter as written: each field or metadata path given a scalar, <c>null</c>, <c>"*"</c> or a list
    /// counts one (an <c>in</c> list or containment list is one condition, whatever its length), and so does each operator
    /// in an operator object; <c>AND</c>, <c>OR</c> and <c>NOT</c> add their children's conditions, and keys that are not a
    /// field of the resource count nothing. The cap is shared so every provider accepts the same filters: a SQL provider
    /// compiles each condition into its own subquery, and planning a statement of many hundreds of them takes longer than
    /// a request may.
    /// </remarks>
    public const int MaxLeaves = 128;

    private const int MaxDepth = StrictJsonData.DefaultMaxDepth;

    private const string Wildcard = "*";

    private static readonly JsonDocumentOptions StrictDocument = new() { AllowDuplicateProperties = false };

    private static readonly HashSet<string> OperatorKeys =
        ["gt", "gte", "lt", "lte", "ne", "in", "contains", "icontains"];

    /// <summary>
    /// A normalized operand: <see cref="Value"/>, or a non-zero <see cref="Overflow"/> sign for an integer beyond the
    /// <see cref="decimal"/> range.
    /// </summary>
    private readonly record struct Operand(JsonValue? Value, int Overflow = 0);

    /// <summary>
    /// Parses <paramref name="filters"/> for <paramref name="kind"/>. This in-process overload accepts strict JSON data
    /// only, so a constructed filter means what the same JSON means over HTTP.
    /// </summary>
    /// <remarks>
    /// Accepted: <c>null</c>; <see cref="JsonObject"/> and <see cref="JsonArray"/> nodes, nested at most 64 deep; and a
    /// <see cref="JsonValue"/> backed by a <see cref="JsonElement"/> (JSON text, parsed strictly), a <see cref="string"/>
    /// or <see cref="char"/>, a <see cref="bool"/>, <see cref="sbyte"/>, <see cref="byte"/>, <see cref="short"/>,
    /// <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, <see cref="ulong"/>,
    /// <see cref="float"/>, <see cref="double"/> (finite) or <see cref="decimal"/>, or a <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/> or <see cref="Guid"/> (written as ISO 8601 or the canonical Guid text). Any other
    /// backing type (collections, dictionaries, POCOs, enums, <see cref="TimeSpan"/>, nested
    /// <see cref="JsonNode"/>s inside a typed value, and so on) is rejected with a
    /// <see cref="NachosValidationException"/> naming the type. A value is classified by its backing runtime value, so
    /// an interface or base-type projection of a non-allowlisted runtime type is rejected; build such a value with <see cref="JsonObject"/> and
    /// <see cref="JsonArray"/>, or convert it first with <c>JsonSerializer.SerializeToNode</c>; the shared rule is
    /// <see cref="StrictJsonData.ToCanonical"/>. Serialization metadata
    /// or converters attached to a <see cref="JsonValue"/> are ignored, and no caller code runs while a value is
    /// rejected.
    /// </remarks>
    /// <returns>The filter, or null when <paramref name="filters"/> is null or an empty object.</returns>
    /// <exception cref="NachosValidationException">The filter is malformed, holds a value outside the accepted types, or holds an invalid value.</exception>
    public static FilterNode? Parse(JsonNode? filters, ResourceKind kind)
    {
        // StrictJsonData rejects what a writer would silently rewrite or serialize through caller code. Its result is a
        // fresh tree of plain values, so writing it to text cannot run a caller converter, and the parser walks a private copy.
        var canonical = StrictJsonData.ToCanonical(filters, MaxDepth);
        return canonical is null ? null : Parse(canonical.ToJsonString(), kind);
    }

    /// <summary>Parses the filter JSON text <paramref name="json"/> for <paramref name="kind"/>.</summary>
    /// <returns>The filter, or null when <paramref name="json"/> is null, blank, <c>null</c> or an empty object.</returns>
    /// <exception cref="NachosValidationException">
    /// The text is not valid JSON, repeats a property name, or the filter is malformed or holds an invalid value.
    /// </exception>
    public static FilterNode? Parse(string? json, ResourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: StrictDocument);

            // JsonNode decodes strings lazily, so an invalid escape such as a lone surrogate (\uD800) only
            // surfaces when read. Decode everything up front so that it is rejected whichever field it sits in.
            DecodeAll(root);
        }
        catch (JsonException ex)
        {
            throw new NachosValidationException($"Filters are not valid JSON: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new NachosValidationException($"Filters contain an invalid string: {ex.Message}", ex);
        }

        var fields = ResourceFields.For(kind);
        return root switch
        {
            null => null,
            JsonObject { Count: 0 } => null,
            JsonObject obj => ParseObject(RequireLeafCount(obj, fields), fields),
            _ => throw Invalid("Filters must be a JSON object."),
        };
    }

    private static JsonObject RequireLeafCount(JsonObject filter, IReadOnlyDictionary<string, FieldDefinition> fields)
    {
        var leaves = Leaves(filter, fields);
        return leaves <= MaxLeaves
            ? filter
            : throw Invalid($"The filter has {leaves} conditions; the limit is {MaxLeaves}.");
    }

    /// <summary>The conditions of a filter object as <see cref="MaxLeaves"/> counts them; malformed parts count as one and are rejected by the parse.</summary>
    private static long Leaves(JsonObject filter, IReadOnlyDictionary<string, FieldDefinition> fields)
    {
        long leaves = 0;
        foreach (var (key, value) in filter)
        {
            if (key is "AND" or "OR" or "NOT")
            {
                leaves += value is JsonArray children ? children.OfType<JsonObject>().Sum(child => Leaves(child, fields)) : 1;
            }
            else if (fields.TryGetValue(key, out var field))
            {
                leaves += field.Type == FieldType.Metadata
                    ? MetadataRootLeaves(value)
                    : value is JsonObject { Count: > 0 } operators ? operators.Count : 1;
            }
        }

        return leaves;
    }

    private static long MetadataRootLeaves(JsonNode? value) =>
        value is JsonObject root
            ? root.Sum(pair => pair is { Key: "contains", Value: JsonObject contained } ? contained.Sum(inner => MetadataLeaves(inner.Value)) : MetadataLeaves(pair.Value))
            : 1;

    // A nested object without operators is containment per key; an object of operators applies each; anything else is one.
    private static long MetadataLeaves(JsonNode? value) => value switch
    {
        JsonObject { Count: > 0 } obj when obj.All(pair => !OperatorKeys.Contains(pair.Key)) => obj.Sum(pair => MetadataLeaves(pair.Value)),
        JsonObject { Count: > 0 } operators => operators.Count,
        _ => 1,
    };

    private static void DecodeAll(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj)
                {
                    DecodeAll(value);
                }

                break;
            case JsonArray array:
                foreach (var element in array)
                {
                    DecodeAll(element);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                _ = value.GetValue<string>();
                break;
        }
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
            _ => Compare(field, FilterOp.Eq, Normalize(wireName, field, value)),
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

                    children.Add(Compare(field, op, Normalize(wireName, field, operand)));
                    break;
            }
        }

        return Conjunction(children);
    }

    /// <summary>Builds the comparison node, folding in operands beyond the <see cref="decimal"/> range.</summary>
    private static FilterNode Compare(FieldDefinition field, FilterOp op, Operand operand)
    {
        if (operand.Overflow == 0)
        {
            return new FilterNode.Field(field.Column, op, operand.Value);
        }

        // A stored count is a finite decimal, so it is below every huge positive operand and above every huge negative one.
        var storedIsBelow = operand.Overflow > 0;
        var matches = op switch
        {
            FilterOp.Ne => true,
            FilterOp.Lt or FilterOp.Lte => storedIsBelow,
            FilterOp.Gt or FilterOp.Gte => !storedIsBelow,
            _ => false,
        };
        return matches ? new FilterNode.MatchAll() : new FilterNode.MatchNone();
    }

    private static FilterNode ParseIn(string wireName, FieldDefinition field, JsonArray array)
    {
        if (!IsAllowed(field.Type, FilterOp.In))
        {
            throw Invalid($"A list is not allowed for field '{wireName}'.");
        }

        RequireListSize($"Field '{wireName}'", array);

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
                continue;
            }

            var operand = Normalize(wireName, field, element);
            if (operand.Overflow == 0)
            {
                values.Add(operand.Value);
            }
        }

        var isNull = new FilterNode.Field(field.Column, FilterOp.IsNull, null);
        if (values.Count == 0)
        {
            return includesNull ? isNull : new FilterNode.MatchNone();
        }

        var inList = new FilterNode.Field(field.Column, FilterOp.In, values);
        return includesNull ? new FilterNode.Or([inList, isNull]) : inList;
    }

    private static void RequireListSize(string where, JsonArray array)
    {
        if (array.Count > MaxListItems)
        {
            throw Invalid($"{where} has a list of {array.Count} items; the limit is {MaxListItems}.");
        }
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
    private static Operand Normalize(string wireName, FieldDefinition field, JsonNode value)
    {
        var kind = value is JsonValue ? value.GetValueKind() : (JsonValueKind?)null;
        switch (field.Type)
        {
            case FieldType.Text when kind == JsonValueKind.String:
                return new Operand(JsonValue.Create(value.GetValue<string>()));

            case FieldType.Number when kind == JsonValueKind.Number && TryNormalizeCount(value.ToJsonString(), out var number):
                return number;

            case FieldType.Number when kind == JsonValueKind.String
                && IntegerString().IsMatch(value.GetValue<string>())
                && TryNormalizeCount(value.GetValue<string>(), out var parsed):
                return parsed;

            case FieldType.Timestamp when kind == JsonValueKind.String && TryParseTimestamp(value.GetValue<string>(), out var instant):
                return new Operand(JsonValue.Create(instant));

            case FieldType.Boolean when kind is JsonValueKind.True or JsonValueKind.False:
                return new Operand(JsonValue.Create(kind == JsonValueKind.True));

            default:
                throw Invalid($"Field '{wireName}' expects {Describe(field.Type)}.");
        }
    }

    private static string Describe(FieldType type) => type switch
    {
        FieldType.Text => "a string",
        FieldType.Number => "an integral number or an integer string",
        FieldType.Timestamp => "an ISO-8601 date or date-time string",
        FieldType.Boolean => "a boolean (true or false)",
        _ => "a different value",
    };

    [GeneratedRegex(@"\A[+-]?[0-9]+\z")]
    private static partial Regex IntegerString();

    [GeneratedRegex(@"\A(?<sign>[+-]?)(?<int>[0-9]+)(?:\.(?<frac>[0-9]+))?(?:[eE](?<exp>[+-]?[0-9]+))?\z")]
    private static partial Regex NumberText();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})?)?\z")]
    private static partial Regex Iso8601();

    /// <summary>
    /// Normalizes JSON number text with an integral value to a <see cref="decimal"/>, or to an overflow sign when it
    /// is outside the <see cref="decimal"/> range. Returns false for a non-integral value.
    /// </summary>
    private static bool TryNormalizeCount(string text, out Operand operand)
    {
        operand = default;
        var match = NumberText().Match(text);
        if (!match.Success)
        {
            return false;
        }

        // Decide integrality on the digits, so it never depends on floating-point or decimal rounding.
        var digits = match.Groups["int"].Value + match.Groups["frac"].Value;
        var pointPosition = match.Groups["int"].Length + ParseExponent(match.Groups["exp"]);
        var lastNonZero = digits.AsSpan().LastIndexOfAnyExcept('0');

        if (lastNonZero < 0)
        {
            operand = new Operand(JsonValue.Create(0m));
            return true;
        }

        if (lastNonZero >= pointPosition)
        {
            return false;
        }

        operand = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            // Truncate drops the scale of a value such as 5.0, so equal counts serialize identically.
            ? new Operand(JsonValue.Create(decimal.Truncate(value)))
            : new Operand(null, match.Groups["sign"].Value == "-" ? -1 : 1);
        return true;
    }

    /// <summary>The exponent, clamped far beyond any digit string a request can carry.</summary>
    private static long ParseExponent(Group exponent)
    {
        if (!exponent.Success)
        {
            return 0;
        }

        const long Clamp = int.MaxValue;
        return int.TryParse(exponent.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : exponent.Value[0] == '-' ? -Clamp : Clamp;
    }

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
            case null:
                // The metadata column is never unset, so "is null" matches nothing.
                return new FilterNode.MatchNone();

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

            case JsonValue when IsWildcard(value):
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
        var where = $"Metadata key '{string.Join('.', path)}'";
        switch (value)
        {
            case null:
                return new FilterNode.MetadataPath(path, FilterOp.IsNull, null);
            case JsonArray array:
                if (array.Count == 0)
                {
                    throw Invalid($"{where} needs a non-empty list.");
                }

                RequireListSize(where, array);
                foreach (var element in array)
                {
                    RequireScalar($"{where} containment list", element);
                }

                return new FilterNode.MetadataPath(path, FilterOp.JsonContains, array.DeepClone());
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    throw Invalid($"{where} has an empty object.");
                }

                var operatorCount = obj.Count(pair => OperatorKeys.Contains(pair.Key));
                if (operatorCount == 0)
                {
                    return ParseMetadataObject(path, obj);
                }

                return operatorCount == obj.Count
                    ? ParseMetadataOperators(path, obj)
                    : throw Invalid($"{where} mixes operators and nested keys.");
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

        RequireListSize(where, array);

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
