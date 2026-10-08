using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions.Filtering;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Filtering;

/// <summary>
/// One compilation of <see cref="SqlFilterCompiler"/>: writes the predicate text and collects its parameters. Every
/// interpolated name in the SQL below is a generated alias, a generated parameter name or the caller's checked table
/// alias; operands only ever reach <see cref="Parameter"/>.
/// </summary>
internal sealed class FilterWriter(ResourceKind kind, string table)
{
    public const string True = "(1 = 1)";
    public const string False = "(1 = 0)";

    /// <summary>Non-supplementary binary collation: compares, measures and slices text by UTF-16 code unit.</summary>
    private const string Bin2 = "Latin1_General_100_BIN2";

    /// <summary>The longest <c>LIKE</c> pattern SQL Server accepts (8000 bytes of <c>nvarchar</c>).</summary>
    private const int MaxLikePattern = 4000;

    private const string ListColumns = "WITH (t int '$.t', k nvarchar(max) '$.k')";

    private readonly List<SqlParameter> _parameters = [];
    private readonly Dictionary<(SqlDbType Type, string Value), string> _shared = [];
    private int _aliases;

    public IReadOnlyList<SqlParameter> Parameters => _parameters;

    public string Write(FilterNode node) => node switch
    {
        FilterNode.And and => and.Children.Count == 0 ? True : Join(and.Children.Select(Write), " AND "),
        FilterNode.Or or => or.Children.Count == 0 ? False : Join(Alternatives(or.Children), " OR "),
        FilterNode.Not not => not.Children.Count == 0 ? True : $"(NOT {Join(Alternatives(not.Children), " OR ")})",
        FilterNode.MatchAll => True,
        FilterNode.MatchNone => False,
        FilterNode.Field field => WriteField(field),
        FilterNode.MetadataPath path => WriteMetadata(path),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown filter node."),
    };

    private static string Join(IEnumerable<string> parts, string separator) => $"({string.Join(separator, parts)})";

    /// <summary>
    /// The children of an OR (or NOT), with metadata equalities on one path merged into a single list match: the parser
    /// turns a metadata <c>in</c> into such equalities, and one list parameter keeps a 1,000-value list cheap.
    /// </summary>
    private IEnumerable<string> Alternatives(IReadOnlyList<FilterNode> children)
    {
        var equalities = children
            .OfType<FilterNode.MetadataPath>()
            .Where(path => path.Op == FilterOp.Eq && Reachable(path.Path))
            .GroupBy(path => new JsonArray([.. path.Path.Select(key => (JsonNode)key)]).ToJsonString(), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();
        var merged = equalities.SelectMany(group => group).ToHashSet(ReferenceEqualityComparer.Instance);

        foreach (var group in equalities)
        {
            yield return MetadataExists(group.First().Path, row => ListMatch(row, ListParameter(group.Select(path => path.Value!))));
        }

        foreach (var child in children.Where(child => !merged.Contains(child)))
        {
            yield return Write(child);
        }
    }

    // ------------------------------------------------------------------------------------------------ columns

    private string WriteField(FilterNode.Field field) => (kind, field.Column) switch
    {
        (not ResourceKind.Message, FilterColumns.Name) => TextColumn($"{table}.Name", binary: true, field),
        (ResourceKind.Message, FilterColumns.PublicId) => TextColumn($"{table}.PublicId", binary: true, field),
        (ResourceKind.Message, FilterColumns.Content) => TextColumn($"{table}.Content", binary: false, field),
        (ResourceKind.Message, FilterColumns.SessionId) => RelatedName("dbo.Sessions", $"{table}.SessionId", field),
        (ResourceKind.Message, FilterColumns.PeerId) => RelatedName("dbo.Peers", $"{table}.PeerId", field),
        (ResourceKind.Message, FilterColumns.TokenCount) => OrderedColumn($"{table}.TokenCount", field, ColumnType.TokenCount),
        (ResourceKind.Session, FilterColumns.PeerId) => ActiveMemberNames(field),
        (ResourceKind.Session, FilterColumns.IsActive) => IsActive(field),
        (_, FilterColumns.CreatedAt) => OrderedColumn($"{table}.CreatedAt", field, ColumnType.Timestamp),
        _ => throw new NotSupportedException($"Column '{field.Column}' is not filterable on a {kind}."),
    };

    /// <summary>A text column that is never NULL.</summary>
    /// <param name="binary">True when the column's own collation is binary, so equality may use it (and its index).</param>
    private string TextColumn(string column, bool binary, FilterNode.Field field) => field.Op switch
    {
        FilterOp.IsNull => False,
        FilterOp.NotNull => True,
        FilterOp.Ne => $"(NOT {TextCondition(column, binary, FilterOp.Eq, field.Value!)})",
        _ => TextCondition(column, binary, field.Op, field.Value!),
    };

    /// <summary>A positive text comparison (<c>eq in contains icontains</c>) on a non-null text expression.</summary>
    private string TextCondition(string text, bool binary, FilterOp op, JsonNode operand)
    {
        switch (op)
        {
            case FilterOp.Eq:
                var value = Text(operand.GetValue<string>());
                return $"({(binary ? text : $"{text} COLLATE {Bin2}")} = {value} AND DATALENGTH({text}) = DATALENGTH({value}))";
            case FilterOp.In:
                var list = Parameter(SqlDbType.NVarChar, operand.ToJsonString(), SqlParameters.LongText);
                var item = Alias();
                return $"EXISTS (SELECT 1 FROM OPENJSON({list}) AS {item} WHERE {item}.[value] COLLATE {Bin2} = {text} AND DATALENGTH({item}.[value]) = DATALENGTH({text}))";
            case FilterOp.Contains:
                return Contains(text, operand.GetValue<string>(), fold: false);
            case FilterOp.IContains:
                return Contains(text, operand.GetValue<string>(), fold: true);
            default:
                throw new NotSupportedException($"Operator {op} does not apply to text.");
        }
    }

    /// <summary>The name of the row a foreign key points at (a message's session or sender): never unset.</summary>
    private string RelatedName(string relatedTable, string foreignKey, FilterNode.Field field)
    {
        string Exists(FilterOp op)
        {
            var row = Alias();
            return $"EXISTS (SELECT 1 FROM {relatedTable} AS {row} WHERE {row}.Id = {foreignKey} AND {TextCondition($"{row}.Name", binary: true, op, field.Value!)})";
        }

        return field.Op switch
        {
            FilterOp.IsNull => False,
            FilterOp.NotNull => True,
            FilterOp.Ne => $"(NOT {Exists(FilterOp.Eq)})",
            _ => Exists(field.Op),
        };
    }

    /// <summary>
    /// A session's <c>PeerId</c>: existential over its active members' names. <c>ne</c> negates <c>eq</c> (so it includes
    /// sessions without active members), and the null checks test whether there are any.
    /// </summary>
    private string ActiveMemberNames(FilterNode.Field field)
    {
        string AnyMember(FilterOp? op)
        {
            var member = Alias();
            var peer = Alias();
            var condition = op is { } positive ? $" AND {TextCondition($"{peer}.Name", binary: true, positive, field.Value!)}" : string.Empty;
            return $"EXISTS (SELECT 1 FROM dbo.SessionPeers AS {member} JOIN dbo.Peers AS {peer} ON {peer}.WorkspaceId = {member}.WorkspaceId AND {peer}.Id = {member}.PeerId WHERE {member}.WorkspaceId = {table}.WorkspaceId AND {member}.SessionId = {table}.Id AND {member}.LeftAt IS NULL{condition})";
        }

        return field.Op switch
        {
            FilterOp.IsNull => $"(NOT {AnyMember(null)})",
            FilterOp.NotNull => AnyMember(null),
            FilterOp.Ne => $"(NOT {AnyMember(FilterOp.Eq)})",
            _ => AnyMember(field.Op),
        };
    }

    /// <summary>A session's liveness: <c>LifecycleState = 0</c> (Active); Inactive and Deleting are not active.</summary>
    private string IsActive(FilterNode.Field field)
    {
        string Is(bool active) => active ? $"({table}.LifecycleState = 0)" : $"({table}.LifecycleState <> 0)";
        return field.Op switch
        {
            FilterOp.IsNull => False,
            FilterOp.NotNull => True,
            FilterOp.Eq => Is(field.Value!.GetValue<bool>()),
            FilterOp.Ne => Is(!field.Value!.GetValue<bool>()),
            _ => throw new NotSupportedException($"Operator {field.Op} does not apply to is_active."),
        };
    }

    private enum ColumnType
    {
        TokenCount,
        Timestamp,
    }

    /// <summary>A never-NULL number or timestamp column, compared with an operand of the column's normalized type.</summary>
    private string OrderedColumn(string column, FilterNode.Field field, ColumnType type)
    {
        string Value(JsonNode operand) => type == ColumnType.TokenCount
            ? Parameter(SqlDbType.Decimal, operand.GetValue<decimal>().ToString(CultureInfo.InvariantCulture), (name, _) => SqlParameters.Decimal(name, operand.GetValue<decimal>()))
            : Parameter(SqlDbType.DateTimeOffset, operand.GetValue<DateTimeOffset>().ToString("O", CultureInfo.InvariantCulture), (name, _) => SqlParameters.Time(name, operand.GetValue<DateTimeOffset>()));

        string Compare(string op) => $"({column} {op} {Value(field.Value!)})";

        switch (field.Op)
        {
            case FilterOp.IsNull:
                return False;
            case FilterOp.NotNull:
                return True;
            case FilterOp.Eq:
                return Compare("=");
            case FilterOp.Ne:
                return Compare("<>");
            case FilterOp.Gt:
                return Compare(">");
            case FilterOp.Gte:
                return Compare(">=");
            case FilterOp.Lt:
                return Compare("<");
            case FilterOp.Lte:
                return Compare("<=");
            case FilterOp.In:
                var elements = new JsonArray([.. field.Value!.AsArray().Select(element => type == ColumnType.TokenCount
                    ? (JsonNode)JsonValue.Create(element!.GetValue<decimal>())
                    : JsonValue.Create(element!.GetValue<DateTimeOffset>().ToString("O", CultureInfo.InvariantCulture)))]);
                var list = Parameter(SqlDbType.NVarChar, elements.ToJsonString(), SqlParameters.LongText);
                var item = Alias();
                var sqlType = type == ColumnType.TokenCount ? "decimal(38, 0)" : "datetimeoffset(7)";
                return $"EXISTS (SELECT 1 FROM OPENJSON({list}) WITH (v {sqlType} '$') AS {item} WHERE {item}.v = {column})";
            default:
                throw new NotSupportedException($"Operator {field.Op} does not apply to {column}.");
        }
    }

    // ------------------------------------------------------------------------------------------------ text helpers

    /// <summary>Ordinal substring (or, with <paramref name="fold"/>, <c>UPPER</c>-folded substring) of non-null text.</summary>
    private string Contains(string text, string operand, bool fold)
    {
        var escaped = EscapeLike(operand);
        if (escaped.Length + 2 <= MaxLikePattern)
        {
            var pattern = Text($"%{escaped}%");
            return fold
                ? $"(UPPER({text} COLLATE {Bin2}) LIKE UPPER({pattern}) ESCAPE N'\\')"
                : $"({text} COLLATE {Bin2} LIKE {pattern} ESCAPE N'\\')";
        }

        // Too long for a LIKE pattern: compare the operand with every substring of its length, positions numbered by
        // OPENJSON over an array of that many elements. Text shorter than the operand yields a NULL array and no rows.
        var value = Text(operand);
        var length = Parameter(SqlDbType.Int, operand.Length.ToString(CultureInfo.InvariantCulture), (name, _) => SqlParameters.Int(name, operand.Length));
        var position = Alias();
        var slice = $"SUBSTRING({text} COLLATE {Bin2}, CAST({position}.[key] AS int) + 1, {length})";
        var match = fold ? $"UPPER({slice}) = UPPER({value} COLLATE {Bin2})" : $"{slice} = {value} COLLATE {Bin2}";
        return $"EXISTS (SELECT 1 FROM OPENJSON(N'[' + REPLICATE(CAST(N'0,' AS nvarchar(max)), DATALENGTH({text}) / 2 - {length}) + N'0]') AS {position} WHERE {match})";
    }

    /// <summary>Escapes the <c>LIKE</c> metacharacters <c>%</c>, <c>_</c>, <c>[</c> and the escape character <c>\</c>.</summary>
    private static string EscapeLike(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var character in text)
        {
            if (character is '\\' or '%' or '_' or '[')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Ordinal comparison of non-null text with an operand as -1, 0 or 1. Equal-length prefixes compare exactly (no
    /// space padding applies), and a proper prefix orders first, as in <see cref="string.CompareOrdinal(string, string)"/>.
    /// </summary>
    private string CompareText(string text, string operand)
    {
        var value = Text(operand);
        var length = Parameter(SqlDbType.Int, operand.Length.ToString(CultureInfo.InvariantCulture), (name, _) => SqlParameters.Int(name, operand.Length));
        var own = $"(DATALENGTH({text}) / 2)";
        var common = $"(CASE WHEN {own} < {length} THEN {own} ELSE {length} END)";
        var left = $"SUBSTRING({text} COLLATE {Bin2}, 1, {common})";
        var right = $"SUBSTRING({value} COLLATE {Bin2}, 1, {common})";
        return $"(CASE WHEN {left} < {right} THEN -1 WHEN {left} > {right} THEN 1 ELSE SIGN({own} - {length}) END)";
    }

    // ------------------------------------------------------------------------------------------------ metadata

    /// <summary>The value at a path inside the metadata: the final <c>OPENJSON</c> row, plus its number key on demand.</summary>
    private sealed class MetadataRow(FilterWriter writer, string row, StringBuilder from)
    {
        private string? _numberKey;

        public string Value => $"{row}.[value]";

        public string Type => $"{row}.[type]";

        /// <summary>The value's number key, joined into the row's FROM clause the first time it is needed.</summary>
        public string NumberKey => _numberKey ??= writer.NumberKey(Value, Type, from);
    }

    /// <summary>False when a key is longer than any stored key can be (see <see cref="SqlJson.MaxKeyLength"/>).</summary>
    private static bool Reachable(IReadOnlyList<string> path) => path.All(key => key.Length <= SqlJson.MaxKeyLength);

    private string WriteMetadata(FilterNode.MetadataPath path)
    {
        if (!Reachable(path.Path))
        {
            // The key cannot exist, so the value is unset.
            return path.Op is FilterOp.IsNull or FilterOp.Ne ? True : False;
        }

        var operand = path.Value;
        return path.Op switch
        {
            FilterOp.NotNull => MetadataExists(path.Path, row => $"{row.Type} <> 0"),
            FilterOp.IsNull => $"(NOT {MetadataExists(path.Path, row => $"{row.Type} <> 0")})",
            FilterOp.Eq => MetadataExists(path.Path, row => ScalarEquals(row, operand!)),
            FilterOp.Ne => $"(NOT {MetadataExists(path.Path, row => ScalarEquals(row, operand!))})",
            FilterOp.Gt => MetadataExists(path.Path, row => Ordered(row, operand!, ">")),
            FilterOp.Gte => MetadataExists(path.Path, row => Ordered(row, operand!, ">=")),
            FilterOp.Lt => MetadataExists(path.Path, row => Ordered(row, operand!, "<")),
            FilterOp.Lte => MetadataExists(path.Path, row => Ordered(row, operand!, "<=")),
            FilterOp.Contains => MetadataExists(path.Path, row => MetadataContains(row, operand!.GetValue<string>(), fold: false)),
            FilterOp.IContains => MetadataExists(path.Path, row => MetadataContains(row, operand!.GetValue<string>(), fold: true)),
            FilterOp.JsonContains => MetadataExists(path.Path, row => ArrayContainsAll(row, ListParameter(operand!.AsArray()!))),
            _ => throw new NotSupportedException($"Operator {path.Op} does not apply to metadata."),
        };
    }

    /// <summary>
    /// <c>EXISTS</c> over the value at <paramref name="path"/> that satisfies <paramref name="condition"/>. Each step is an
    /// <c>OPENJSON</c> of the previous value, opened only when that value is an object (<c>type = 5</c>) under the
    /// wanted key, so a key never matches inside an array or a scalar. Keys compare exactly (binary and length).
    /// </summary>
    private string MetadataExists(IReadOnlyList<string> path, Func<MetadataRow, string> condition)
    {
        var from = new StringBuilder();
        var where = new List<string>(path.Count + 1);
        string? previous = null;
        string? previousKey = null;
        foreach (var key in path)
        {
            var step = Alias();
            var keyParameter = Text(key);
            from.Append(previous is null
                ? $"FROM OPENJSON({table}.Metadata) AS {step}"
                : $" CROSS APPLY OPENJSON(CASE WHEN {previous}.[type] = 5 AND {KeyMatches(previous, previousKey!)} THEN {previous}.[value] END) AS {step}");
            where.Add(KeyMatches(step, keyParameter));
            previous = step;
            previousKey = keyParameter;
        }

        var row = new MetadataRow(this, previous!, from);
        where.Add(condition(row));
        return $"EXISTS (SELECT 1 {from} WHERE {string.Join(" AND ", where)})";
    }

    private static string KeyMatches(string row, string keyParameter) =>
        $"{row}.[key] = {keyParameter} AND DATALENGTH({row}.[key]) = DATALENGTH({keyParameter})";

    /// <summary>Equality with a scalar operand of the same JSON kind.</summary>
    private string ScalarEquals(MetadataRow row, JsonNode operand) => operand.GetValueKind() switch
    {
        JsonValueKind.String => $"({row.Type} = 1 AND {row.Value} COLLATE {Bin2} = {Text(operand.GetValue<string>())} AND DATALENGTH({row.Value}) = DATALENGTH({Text(operand.GetValue<string>())}))",
        JsonValueKind.Number => $"({row.Type} = 2 AND {CompareNumber(row.NumberKey, operand)} = 0)",
        JsonValueKind.True => $"({row.Type} = 3 AND {row.Value} = N'true')",
        JsonValueKind.False => $"({row.Type} = 3 AND {row.Value} = N'false')",
        var other => throw new NotSupportedException($"A metadata operand of kind {other} cannot be compared."),
    };

    /// <summary>An ordering comparison with a string or number operand of the same JSON kind.</summary>
    private string Ordered(MetadataRow row, JsonNode operand, string op) => operand.GetValueKind() switch
    {
        JsonValueKind.String => $"({row.Type} = 1 AND {CompareText(row.Value, operand.GetValue<string>())} {op} 0)",
        JsonValueKind.Number => $"({row.Type} = 2 AND {CompareNumber(row.NumberKey, operand)} {op} 0)",
        var other => throw new NotSupportedException($"A metadata operand of kind {other} cannot be ordered."),
    };

    /// <summary>A string containing the text, or an array with a string element equal to it (both under the same folding).</summary>
    private string MetadataContains(MetadataRow row, string operand, bool fold)
    {
        var element = Alias();
        var value = Text(operand);
        var equal = fold
            ? $"UPPER({element}.[value] COLLATE {Bin2}) = UPPER({value} COLLATE {Bin2})"
            : $"{element}.[value] COLLATE {Bin2} = {value}";
        return $"(({row.Type} = 1 AND {Contains(row.Value, operand, fold)}) OR ({row.Type} = 4 AND EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN {row.Type} = 4 THEN {row.Value} END) AS {element} WHERE {element}.[type] = 1 AND {equal} AND DATALENGTH({element}.[value]) = DATALENGTH({value}))))";
    }

    /// <summary>The value is an array holding an element equal (same kind) to every element of the list.</summary>
    private string ArrayContainsAll(MetadataRow row, string list)
    {
        var wanted = Alias();
        var element = Alias();
        var from = new StringBuilder($"FROM OPENJSON(CASE WHEN {row.Type} = 4 THEN {row.Value} END) AS {element}");
        var key = NumberKey($"{element}.[value]", $"{element}.[type]", from);
        return $"({row.Type} = 4 AND NOT EXISTS (SELECT 1 FROM OPENJSON({list}) {ListColumns} AS {wanted} WHERE NOT EXISTS (SELECT 1 {from} WHERE {ElementMatches($"{element}.[value]", $"{element}.[type]", key, wanted)})))";
    }

    /// <summary>The value equals (same kind) some element of the list.</summary>
    private string ListMatch(MetadataRow row, string list)
    {
        var wanted = Alias();
        return $"EXISTS (SELECT 1 FROM OPENJSON({list}) {ListColumns} AS {wanted} WHERE {ElementMatches(row.Value, row.Type, row.NumberKey, wanted)})";
    }

    /// <summary>
    /// Kind-and-value equality of a JSON value with a list element <c>{t, k}</c> (see <see cref="ListParameter"/>).
    /// </summary>
    private static string ElementMatches(string value, string type, string key, string wanted) =>
        $"(({type} = 1 AND {wanted}.t = 1 AND {value} COLLATE {Bin2} = {wanted}.k COLLATE {Bin2} AND DATALENGTH({value}) = DATALENGTH({wanted}.k))"
        + $" OR ({type} = 2 AND {wanted}.t = 2 AND {key} = {wanted}.k COLLATE {Bin2})"
        + $" OR ({type} = 3 AND {wanted}.t = 3 AND {value} = {wanted}.k))";

    /// <summary>
    /// A list of scalar operands as one JSON parameter of <c>{t, k}</c> objects: <c>t</c> is the <c>OPENJSON</c> type
    /// (1 string, 2 number, 3 boolean) and <c>k</c> the string, the number's order key (see
    /// <see cref="ExactDecimal.ToOrderKey"/>) or <c>true</c>/<c>false</c>.
    /// </summary>
    private string ListParameter(IEnumerable<JsonNode> operands)
    {
        var list = new JsonArray();
        foreach (var operand in operands)
        {
            list.Add(operand.GetValueKind() switch
            {
                JsonValueKind.String => new JsonObject { ["t"] = 1, ["k"] = operand.GetValue<string>() },
                JsonValueKind.Number => new JsonObject { ["t"] = 2, ["k"] = ExactDecimal.Parse(operand.ToJsonString()).ToOrderKey() },
                JsonValueKind.True => new JsonObject { ["t"] = 3, ["k"] = "true" },
                JsonValueKind.False => new JsonObject { ["t"] = 3, ["k"] = "false" },
                var other => throw new NotSupportedException($"A metadata list element of kind {other} cannot be compared."),
            });
        }

        return Parameter(SqlDbType.NVarChar, list.ToJsonString(), SqlParameters.LongText);
    }

    // ------------------------------------------------------------------------------------------------ numbers

    /// <summary>
    /// Appends to <paramref name="from"/> the order key of a stored JSON number, computed by the schema function
    /// <c>dbo.JsonNumberOrderKey</c> from the number's text, and returns the key expression. The key is exact for any
    /// number text, of any length or exponent; <see cref="ExactDecimal.ToOrderKey"/> documents it and computes the same
    /// key for operands.
    /// </summary>
    /// <remarks>
    /// <c>OPENJSON</c> over <c>nvarchar(max)</c> returns a number's text exactly as stored, at any length (unlike
    /// <c>JSON_VALUE</c>, which returns at most 4000 characters). Keys compare under a binary collation, where SQL's space
    /// padding makes a proper prefix sort first, as the key requires. Rows that are not numbers pass NULL and get a NULL
    /// key, and every caller tests <c>type = 2</c> first, so a NULL never decides a match.
    /// </remarks>
    private string NumberKey(string value, string type, StringBuilder from)
    {
        var key = Alias();
        from.Append(CultureInfo.InvariantCulture, $" CROSS APPLY (SELECT dbo.JsonNumberOrderKey(CASE WHEN {type} = 2 THEN {value} END) COLLATE {Bin2} AS k) AS {key}");
        return $"{key}.k";
    }

    /// <summary>Exact comparison of a stored number's key with a number operand as -1, 0 or 1.</summary>
    private string CompareNumber(string stored, JsonNode operand)
    {
        var key = Parameter(SqlDbType.VarChar, ExactDecimal.Parse(operand.ToJsonString()).ToOrderKey(), SqlParameters.Ascii);
        return $"(CASE WHEN {stored} = {key} THEN 0 WHEN {stored} < {key} THEN -1 ELSE 1 END)";
    }

    // ------------------------------------------------------------------------------------------------ names

    private string Alias() => $"fa{_aliases++}";

    private string Text(string value) => Parameter(SqlDbType.NVarChar, value, SqlParameters.Text);

    /// <summary>
    /// The parameter holding <paramref name="value"/>, shared with any earlier parameter of the same type and value.
    /// </summary>
    private string Parameter(SqlDbType type, string value, Func<string, string, SqlParameter> create)
    {
        if (_shared.TryGetValue((type, value), out var existing))
        {
            return existing;
        }

        var name = $"@f{_parameters.Count}";
        _parameters.Add(create(name, value));
        _shared.Add((type, value), name);
        return name;
    }
}
