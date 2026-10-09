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

    private readonly List<SqlParameter> _parameters = [];
    private readonly Dictionary<(SqlDbType Type, string Value), string> _shared = [];
    private int _aliases;

    public IReadOnlyList<SqlParameter> Parameters => _parameters;

    public string Write(FilterNode node) => node switch
    {
        FilterNode.And and => and.Children.Count == 0 ? True : Join(Conjuncts(and.Children), " AND "),
        FilterNode.Or or => or.Children.Count == 0 ? False : Join(Alternatives(or.Children), " OR "),
        FilterNode.Not not => not.Children.Count == 0 ? True : $"(NOT {Join(Alternatives(not.Children), " OR ")})",
        FilterNode.MatchAll => True,
        FilterNode.MatchNone => False,
        FilterNode.Field field => WriteField(field),
        FilterNode.MetadataPath path => WriteMetadata(path),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown filter node."),
    };

    private static string Join(IEnumerable<string> parts, string separator) => $"({string.Join(separator, parts)})";

    /// <summary>The metadata operators that are a positive condition on the value at their path (no <c>NOT</c> around it).</summary>
    private static bool IsPositive(FilterOp op) => op is not (FilterOp.Ne or FilterOp.IsNull);

    /// <summary>
    /// The children of an AND, with positive metadata conditions on one path merged into a single <c>EXISTS</c>: a stored
    /// object never repeats a key (stored JSON is canonical), so <c>EXISTS(p, a) AND EXISTS(p, b)</c> is
    /// <c>EXISTS(p, a AND b)</c>, and the merged form resolves the path, and computes a number's key, once instead of per
    /// condition (a range such as <c>{"gte": 1, "lt": 2}</c> is two conditions).
    /// </summary>
    private IEnumerable<string> Conjuncts(IReadOnlyList<FilterNode> children)
    {
        var groups = children
            .OfType<FilterNode.MetadataPath>()
            .Where(path => IsPositive(path.Op) && Reachable(path.Path))
            .GroupBy(path => new JsonArray([.. path.Path.Select(key => (JsonNode)key)]).ToJsonString(), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();
        var merged = groups.SelectMany(group => group).ToHashSet(ReferenceEqualityComparer.Instance);

        foreach (var group in groups)
        {
            yield return MetadataExists(group.First().Path, row => string.Join(" AND ", group.Select(path => PositiveCondition(row, path))));
        }

        foreach (var child in children.Where(child => !merged.Contains(child)))
        {
            yield return Write(child);
        }
    }

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
            yield return MetadataExists(group.First().Path, row => ListMatch(row, [.. group.Select(path => path.Value!)]));
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

        // Too long for a LIKE pattern. A LIKE on the longest prefix that fits picks the candidates cheaply (containing the
        // operand implies containing its prefix, folded or not); only those are scanned exactly, comparing the operand with
        // every substring of its length, positions numbered by OPENJSON over an array of that many elements. CASE fixes
        // the order of evaluation, so the scan never runs for a row the prefix rules out.
        var prefixPattern = Text($"%{LongestLikePrefix(operand)}%");
        var candidate = fold
            ? $"UPPER({text} COLLATE {Bin2}) LIKE UPPER({prefixPattern}) ESCAPE N'\\'"
            : $"{text} COLLATE {Bin2} LIKE {prefixPattern} ESCAPE N'\\'";
        var value = Text(operand);
        var length = Parameter(SqlDbType.Int, operand.Length.ToString(CultureInfo.InvariantCulture), (name, _) => SqlParameters.Int(name, operand.Length));
        var position = Alias();
        var slice = $"SUBSTRING({text} COLLATE {Bin2}, CAST({position}.[key] AS int) + 1, {length})";
        var match = fold ? $"UPPER({slice}) = UPPER({value} COLLATE {Bin2})" : $"{slice} = {value} COLLATE {Bin2}";
        var scan = $"EXISTS (SELECT 1 FROM OPENJSON(N'[' + REPLICATE(CAST(N'0,' AS nvarchar(max)), DATALENGTH({text}) / 2 - {length}) + N'0]') AS {position} WHERE {match})";
        return $"(CASE WHEN {candidate} THEN CASE WHEN {scan} THEN 1 ELSE 0 END ELSE 0 END = 1)";
    }

    /// <summary>
    /// The escaped form of the longest prefix of <paramref name="operand"/> that fits a <c>LIKE</c> pattern with a
    /// <c>%</c> on each side. It never ends inside a surrogate pair.
    /// </summary>
    private static string LongestLikePrefix(string operand)
    {
        var escaped = new StringBuilder(MaxLikePattern);
        for (var i = 0; i < operand.Length; i++)
        {
            var take = char.IsHighSurrogate(operand[i]) && i + 1 < operand.Length ? 2 : 1;
            var piece = EscapeLike(operand.Substring(i, take));
            if (escaped.Length + piece.Length + 2 > MaxLikePattern)
            {
                break;
            }

            escaped.Append(piece);
            i += take - 1;
        }

        return escaped.ToString();
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

    /// <summary>The value at a path inside the metadata: the final <c>OPENJSON</c> row, plus derived key columns on demand.</summary>
    private sealed class MetadataRow(FilterWriter writer, string row, StringBuilder from)
    {
        private string? _numberKey;

        public string Value => $"{row}.[value]";

        public string Type => $"{row}.[type]";

        /// <summary>The value's number key, joined into the row's FROM clause the first time it is needed (once per row).</summary>
        public string NumberKey => _numberKey ??= writer.NumberKey(Value, Type, from);

        /// <summary>The number keys of the elements of <paramref name="array"/>, joined into the row's FROM clause.</summary>
        public string ArrayNumberKeys(string array) => writer.ArrayNumberKeys(array, from);
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
            FilterOp.IsNull => $"(NOT {MetadataExists(path.Path, row => $"{row.Type} <> 0")})",
            FilterOp.Ne => $"(NOT {MetadataExists(path.Path, row => ScalarEquals(row, operand!))})",
            _ => MetadataExists(path.Path, row => PositiveCondition(row, path)),
        };
    }

    /// <summary>The condition a positive operator puts on the value at its path (see <see cref="IsPositive"/>).</summary>
    private string PositiveCondition(MetadataRow row, FilterNode.MetadataPath path)
    {
        var operand = path.Value;
        return path.Op switch
        {
            FilterOp.NotNull => $"{row.Type} <> 0",
            FilterOp.Eq => ScalarEquals(row, operand!),
            FilterOp.Gt => Ordered(row, operand!, ">"),
            FilterOp.Gte => Ordered(row, operand!, ">="),
            FilterOp.Lt => Ordered(row, operand!, "<"),
            FilterOp.Lte => Ordered(row, operand!, "<="),
            FilterOp.Contains => MetadataContains(row, operand!.GetValue<string>(), fold: false),
            FilterOp.IContains => MetadataContains(row, operand!.GetValue<string>(), fold: true),
            FilterOp.JsonContains => ArrayContainsAll(row, [.. operand!.AsArray().Select(element => element!)]),
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
        JsonValueKind.Number => $"({row.Type} = 2 AND {CompareNumber(row.NumberKey, operand, "=")})",
        JsonValueKind.True => $"({row.Type} = 3 AND {row.Value} = N'true')",
        JsonValueKind.False => $"({row.Type} = 3 AND {row.Value} = N'false')",
        var other => throw new NotSupportedException($"A metadata operand of kind {other} cannot be compared."),
    };

    /// <summary>An ordering comparison with a string or number operand of the same JSON kind.</summary>
    private string Ordered(MetadataRow row, JsonNode operand, string op) => operand.GetValueKind() switch
    {
        JsonValueKind.String => $"({row.Type} = 1 AND {CompareText(row.Value, operand.GetValue<string>())} {op} 0)",
        JsonValueKind.Number => $"({row.Type} = 2 AND {CompareNumber(row.NumberKey, operand, op)})",
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

    /// <summary>
    /// The value is an array holding, for every operand, an element of the same kind equal to it. Number elements' keys
    /// are computed once per element (an aggregate over the array), never per (element, operand) pair.
    /// </summary>
    private string ArrayContainsAll(MetadataRow row, IReadOnlyList<JsonNode> operands)
    {
        var array = $"CASE WHEN {row.Type} = 4 THEN {row.Value} END";
        var parts = new List<string> { $"{row.Type} = 4" };

        var strings = operands.Where(o => o.GetValueKind() == JsonValueKind.String).Select(o => o.GetValue<string>()).ToList();
        if (strings.Count > 0)
        {
            var wanted = Alias();
            var element = Alias();
            parts.Add($"NOT EXISTS (SELECT 1 FROM OPENJSON({StringList(strings)}) AS {wanted} WHERE NOT EXISTS (SELECT 1 FROM OPENJSON({array}) AS {element} WHERE {element}.[type] = 1 AND {element}.[value] COLLATE {Bin2} = {wanted}.[value] COLLATE {Bin2} AND DATALENGTH({element}.[value]) = DATALENGTH({wanted}.[value])))");
        }

        foreach (var flag in operands.Select(o => o.GetValueKind()).Where(k => k is JsonValueKind.True or JsonValueKind.False).Distinct())
        {
            var element = Alias();
            parts.Add($"EXISTS (SELECT 1 FROM OPENJSON({array}) AS {element} WHERE {element}.[type] = 3 AND {element}.[value] = {(flag == JsonValueKind.True ? "N'true'" : "N'false'")})");
        }

        var numbers = NumberKeys(operands);
        if (numbers.Count > 0)
        {
            // The array's number keys as one JSON array of strings, built once per row by the aggregate.
            var keys = row.ArrayNumberKeys(array);
            var wanted = Alias();
            var have = Alias();
            parts.Add($"NOT EXISTS (SELECT 1 FROM OPENJSON({KeyList(numbers)}) WITH (k varchar(max) '$') AS {wanted} WHERE NOT EXISTS (SELECT 1 FROM OPENJSON({keys}) WITH (k varchar(max) '$') AS {have} WHERE {have}.k COLLATE {Bin2} = {wanted}.k COLLATE {Bin2}))");
        }

        return $"({string.Join(" AND ", parts)})";
    }

    /// <summary>
    /// The value equals (same kind) some operand. Operands are split by kind: strings are one JSON list, booleans fixed
    /// literals, and numbers a set of order keys tested against the value's key, which is computed once per row.
    /// </summary>
    private string ListMatch(MetadataRow row, IReadOnlyList<JsonNode> operands)
    {
        var parts = new List<string>();
        var strings = operands.Where(o => o.GetValueKind() == JsonValueKind.String).Select(o => o.GetValue<string>()).ToList();
        if (strings.Count > 0)
        {
            var wanted = Alias();
            parts.Add($"({row.Type} = 1 AND EXISTS (SELECT 1 FROM OPENJSON({StringList(strings)}) AS {wanted} WHERE {wanted}.[value] COLLATE {Bin2} = {row.Value} COLLATE {Bin2} AND DATALENGTH({wanted}.[value]) = DATALENGTH({row.Value})))");
        }

        foreach (var flag in operands.Select(o => o.GetValueKind()).Where(k => k is JsonValueKind.True or JsonValueKind.False).Distinct())
        {
            parts.Add($"({row.Type} = 3 AND {row.Value} = {(flag == JsonValueKind.True ? "N'true'" : "N'false'")})");
        }

        var numbers = NumberKeys(operands);
        if (numbers.Count > 0)
        {
            parts.Add($"({row.Type} = 2 AND {KeyIn(row.NumberKey, numbers)})");
        }

        return parts.Count == 0 ? False : $"({string.Join(" OR ", parts)})";
    }

    private string StringList(IEnumerable<string> strings) =>
        Parameter(SqlDbType.NVarChar, new JsonArray([.. strings.Select(s => (JsonNode)s)]).ToJsonString(), SqlParameters.LongText);

    private string KeyList(IEnumerable<string> keys) =>
        Parameter(SqlDbType.NVarChar, new JsonArray([.. keys.Select(k => (JsonNode)k)]).ToJsonString(), SqlParameters.LongText);

    /// <summary>The distinct order keys of the number operands (see <see cref="ExactDecimal.ToOrderKey"/>).</summary>
    private static List<string> NumberKeys(IEnumerable<JsonNode> operands) =>
    [
        .. operands
            .Where(o => o.GetValueKind() == JsonValueKind.Number)
            .Select(o => ExactDecimal.Parse(o.ToJsonString()).ToOrderKey())
            .Distinct(StringComparer.Ordinal),
    ];

    // ------------------------------------------------------------------------------------------------ numbers

    /// <summary>The longest key tested by <c>CHARINDEX</c> in <see cref="KeyIn"/>; longer ones (absurd numbers) take the exact list path.</summary>
    private const int ShortKey = 100;

    /// <summary>The longest <c>varchar</c> value that is not <c>max</c>, where <c>CHARINDEX</c> is about twice as fast.</summary>
    private const int ChunkLength = 8000;

    /// <summary>
    /// Appends to <paramref name="from"/> the order key of a stored JSON number and returns the key column, which may be
    /// referenced any number of times: <c>dbo.JsonNumberOrderKey</c> runs <b>once per row</b>. The key is exact for any
    /// number text; <see cref="ExactDecimal.ToOrderKey"/> documents it and computes the same key for operands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why <c>OPENJSON(JSON_ARRAY(...))</c>.</b> A scalar UDF in a <c>CROSS APPLY (SELECT f(...))</c>, even under
    /// <c>TOP (1)</c>, is a deferred compute scalar: the optimizer copies the call into every place the column is used,
    /// including the inner side of a join over an operand list, so one row costs one call per list element (measured:
    /// 995,050 calls for 10,000 rows and 100 operands). As the argument of a table-valued function the call must run once to
    /// open the rowset, and the key comes back as a plain column. A non-number passes NULL; <c>JSON_ARRAY</c> then yields
    /// <c>[]</c> and the <c>OUTER APPLY</c> a NULL key, and every caller tests <c>type = 2</c> first.
    /// </para>
    /// <para>
    /// <c>OPENJSON</c> over <c>nvarchar(max)</c> returns a number's text exactly as stored, at any length (unlike
    /// <c>JSON_VALUE</c>, which returns at most 4000 characters). Keys compare under a binary collation, where SQL's space
    /// padding makes a proper prefix sort first, as the key requires.
    /// </para>
    /// </remarks>
    private string NumberKey(string value, string type, StringBuilder from)
    {
        var key = Alias();
        from.Append(CultureInfo.InvariantCulture, $" OUTER APPLY OPENJSON(JSON_ARRAY(dbo.JsonNumberOrderKey(CASE WHEN {type} = 2 THEN {value} END))) WITH (k varchar(max) '$') AS {key}");
        return $"{key}.k COLLATE {Bin2}";
    }

    /// <summary>
    /// Appends to <paramref name="from"/> the order keys of the number elements of <paramref name="array"/> as one JSON
    /// array of strings (NULL when there are none) and returns that column. The aggregate consumes each element once, so
    /// <c>dbo.JsonNumberOrderKey</c> runs once per number element.
    /// </summary>
    private string ArrayNumberKeys(string array, StringBuilder from)
    {
        var keys = Alias();
        var element = Alias();
        from.Append(CultureInfo.InvariantCulture, $" OUTER APPLY (SELECT CAST(N'[\"' AS nvarchar(max)) + STRING_AGG(CAST(dbo.JsonNumberOrderKey({element}.[value]) AS nvarchar(max)), N'\",\"') + N'\"]' AS k FROM OPENJSON({array}) AS {element} WHERE {element}.[type] = 2) AS {keys}");
        return $"{keys}.k";
    }

    /// <summary>Single-reference comparison of a stored number's key with a number operand's key.</summary>
    private string CompareNumber(string stored, JsonNode operand, string op)
    {
        var key = Parameter(SqlDbType.VarChar, ExactDecimal.Parse(operand.ToJsonString()).ToOrderKey(), SqlParameters.Ascii);
        return $"({stored} {op} {key})";
    }

    /// <summary>
    /// Whether the key column <paramref name="key"/> is one of <paramref name="keys"/>, without parsing a list per row.
    /// Keys of up to <see cref="ShortKey"/> characters are packed as <c>|k1|k2|…|</c> into <c>varchar(8000)</c> chunks and
    /// found with <c>CHARINDEX</c>; keys hold only digits and <c>:</c>, so a match between delimiters is exact equality.
    /// Longer keys (numbers of about 90 digits or more) go through an exact <c>OPENJSON</c> list.
    /// </summary>
    private string KeyIn(string key, IReadOnlyList<string> keys)
    {
        var parts = new List<string>();
        var needle = $"CASE WHEN LEN({key}) <= {ShortKey.ToString(CultureInfo.InvariantCulture)} THEN CAST('|' + {key} + '|' AS varchar({ChunkLength.ToString(CultureInfo.InvariantCulture)})) END COLLATE {Bin2}";
        var chunk = new StringBuilder("|");
        foreach (var shortKey in keys.Where(k => k.Length <= ShortKey))
        {
            if (chunk.Length + shortKey.Length + 1 > ChunkLength)
            {
                parts.Add($"CHARINDEX({needle}, {Parameter(SqlDbType.VarChar, chunk.ToString(), SqlParameters.Ascii)}) > 0");
                chunk.Clear().Append('|');
            }

            chunk.Append(shortKey).Append('|');
        }

        if (chunk.Length > 1)
        {
            parts.Add($"CHARINDEX({needle}, {Parameter(SqlDbType.VarChar, chunk.ToString(), SqlParameters.Ascii)}) > 0");
        }

        var longKeys = keys.Where(k => k.Length > ShortKey).ToList();
        if (longKeys.Count > 0)
        {
            var wanted = Alias();
            parts.Add($"{key} IN (SELECT {wanted}.k COLLATE {Bin2} FROM OPENJSON({KeyList(longKeys)}) WITH (k varchar(max) '$') AS {wanted})");
        }

        return $"({string.Join(" OR ", parts)})";
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
