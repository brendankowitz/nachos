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
/// alias; operands only ever reach <see cref="Parameter"/> (or <see cref="Bytes"/>).
/// </summary>
/// <param name="digestHexLength">
/// The hex digits of SHA-256 kept in prefilter entries: <see cref="SqlDigest.FullHexLength"/> except in tests, which
/// shorten it to force digest collisions and prove that the exact confirmation decides membership.
/// </param>
internal sealed class FilterWriter(ResourceKind kind, string table, int digestHexLength = SqlDigest.FullHexLength)
{
    public const string True = "(1 = 1)";
    public const string False = "(1 = 0)";

    /// <summary>Non-supplementary binary collation: compares, measures and slices text by UTF-16 code unit.</summary>
    private const string Bin2 = "Latin1_General_100_BIN2";

    /// <summary>The longest <c>nvarchar</c> text <c>CHARINDEX</c> searches for (8000 bytes).</summary>
    private const int MaxCharIndexOperand = 4000;

    private readonly List<SqlParameter> _parameters = [];
    private readonly Dictionary<(SqlDbType Type, string Value), string> _shared = [];
    private int _aliases;

    public IReadOnlyList<SqlParameter> Parameters => _parameters;

    private int DigestHexLength => digestHexLength;

    /// <summary>While <see cref="WriteFilter"/> collects them, the metadata conditions of the filter, each a per-row flag.</summary>
    private List<MetadataFlag>? _flags;

    /// <summary>The alias of the derived table holding the flags (<c>l0</c>, <c>l1</c>, …).</summary>
    private string _flagsAlias = "";

    /// <summary>
    /// The most members one flag holds (<see cref="AnyExists"/>). Measured over 10,000 rows, cold: one flag of 128
    /// members compiled in 0.8 s and of 400 in 10.6 s, while the flags themselves run in about 0.2 s.
    /// </summary>
    private const int FlagMembers = 32;

    /// <summary>A condition on the value at <see cref="Path"/>.</summary>
    private sealed record FlagMember(IReadOnlyList<string> Path, Func<MetadataRow, string> Condition);

    /// <summary>
    /// A flag (see <see cref="WriteWithFlags"/>): some entry of the metadata satisfies one of <see cref="Members"/>, that is the
    /// OR of their <c>EXISTS</c>.
    /// </summary>
    private sealed record MetadataFlag(IReadOnlyList<FlagMember> Members);

    /// <summary>
    /// The predicate for a whole filter. A filter with one metadata condition (after merging) is written directly: an
    /// <c>EXISTS</c> over its path. With more, the metadata is read once per row for all of them (<see cref="WriteWithFlags"/>),
    /// so a row costs one pass over its entries, not one per condition (partner review I2: 128 conditions as separate
    /// <c>EXISTS</c> took minutes over 10,000 rows; measured with flags, cold cache, 10,000 rows: 1000 conditions in
    /// 0.4-4.7 s, and 1990 <c>contains</c> on distinct keys in 5.6 s, almost all of it compilation).
    /// </summary>
    public string WriteFilter(FilterNode node)
    {
        var parameters = _parameters.Count;
        _flags = [];
        _flagsAlias = Alias();
        string formula;
        List<MetadataFlag> flags;
        try
        {
            formula = Write(node);
            flags = _flags;
        }
        finally
        {
            _flags = null;
            _sharedFlags.Clear();
        }

        if (flags.Count > 1 || flags.Any(flag => flag.Members.Count > 1))
        {
            return WriteWithFlags(formula, flags);
        }

        // Write again without flags; drop what the first pass added so every parameter is used.
        foreach (var stale in _shared.Where(pair => int.Parse(pair.Value.AsSpan(2), CultureInfo.InvariantCulture) >= parameters).Select(pair => pair.Key).ToList())
        {
            _shared.Remove(stale);
        }

        _parameters.RemoveRange(parameters, _parameters.Count - parameters);
        return Write(node);
    }

    /// <summary>
    /// <paramref name="formula"/> (the filter's AND/OR/NOT over column conditions and the flags <c>l0</c>, <c>l1</c>, …)
    /// over one aggregate per row: <c>l<i>i</i></c> is 1 when some entry of the metadata satisfies flag <i>i</i>, which is
    /// exactly the <c>EXISTS</c> it replaces. The metadata is opened once. Each entry's key is looked up once among the keys
    /// the flags name (<see cref="KeyIndex"/>); entries under other keys cannot set a flag, so they are not aggregated, and
    /// an entry is tested only against the flags under its own key (nested <c>CASE</c> on the key's index, so a condition
    /// never runs on another key's entry). Derived columns (number keys, digests) are computed once per entry, only under
    /// named keys. A condition deeper than the first key is an <c>EXISTS</c> over the rest of its path, opened only for the
    /// entry under its first key.
    /// </summary>
    private string WriteWithFlags(string formula, IReadOnlyList<MetadataFlag> flags)
    {
        var root = Alias();
        var from = new StringBuilder($"FROM OPENJSON({table}.Metadata) AS {root}");
        var keys = flags.SelectMany(flag => flag.Members).Select(member => member.Path[0]).Distinct(StringComparer.Ordinal).Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
        var index = KeyIndex(root, keys, from);
        var row = new MetadataRow(this, root, from, $"{index} IS NOT NULL");

        var definitions = new List<string>(flags.Count);
        for (var i = 0; i < flags.Count; i++)
        {
            // One branch per first key, so a member's condition only ever runs on the entry under its own key.
            var branches = new StringBuilder();
            foreach (var group in flags[i].Members.GroupBy(member => member.Path[0], StringComparer.Ordinal))
            {
                var key = keys[group.Key].ToString(CultureInfo.InvariantCulture);
                var holds = string.Join(" OR ", group.Select(member => member.Path.Count == 1
                    ? member.Condition(row)
                    : MetadataExists([.. member.Path.Skip(1)], member.Condition, $"CASE WHEN {root}.[type] = 5 AND {index} = {key} THEN {root}.[value] END")));
                branches.Append(CultureInfo.InvariantCulture, $" WHEN {index} = {key} THEN CASE WHEN {holds} THEN 1 ELSE 0 END");
            }

            definitions.Add(string.Create(CultureInfo.InvariantCulture, $"CASE{branches} ELSE 0 END AS f{i}"));
        }

        var element = Alias();
        from.Append(CultureInfo.InvariantCulture, $" CROSS APPLY (SELECT {string.Join(", ", definitions)}) AS {element}");
        var columns = string.Join(", ", Enumerable.Range(0, flags.Count).Select(i => string.Create(CultureInfo.InvariantCulture, $"ISNULL(MAX({element}.f{i}), 0) AS l{i}")));
        return $"EXISTS (SELECT 1 FROM (SELECT {columns} {from} WHERE {index} IS NOT NULL) AS {_flagsAlias} WHERE {formula})";
    }

    /// <summary>
    /// Appends to <paramref name="from"/> the index in <paramref name="keys"/> of the key of the entry <paramref name="row"/>
    /// (NULL for any other key), computed once per entry, and returns that column. Keys of up to <see cref="PackedKeyUnits"/>
    /// code units are packed as <c>hex:index</c> (hex of the UTF-16 code units, so the match is exact, trailing spaces
    /// included) in sorted, range-gated chunks as in <see cref="PackedIn"/>; one <c>CHARINDEX</c> finds the entry's key and
    /// the index beside it. Longer keys are compared directly.
    /// </summary>
    private string KeyIndex(string row, IReadOnlyDictionary<string, int> keys, StringBuilder from)
    {
        var bytes = (PackedKeyUnits * 2).ToString(CultureInfo.InvariantCulture);
        var hex = $"CASE WHEN DATALENGTH({row}.[key]) <= {bytes} THEN CONVERT(varchar({ChunkLength.ToString(CultureInfo.InvariantCulture)}), CAST({row}.[key] AS varbinary({bytes})), 2) END COLLATE {Bin2}";
        var lookups = new List<string>();
        var chunk = new StringBuilder("|");
        string? first = null;
        string? last = null;

        void Flush()
        {
            if (first is null)
            {
                return;
            }

            var low = Parameter(SqlDbType.VarChar, first, SqlParameters.Ascii);
            var high = Parameter(SqlDbType.VarChar, last!, SqlParameters.Ascii);
            var packed = Parameter(SqlDbType.VarChar, chunk.ToString(), SqlParameters.Ascii);
            lookups.Add($"CASE WHEN {hex} >= {low} AND {hex} <= {high} THEN CAST(SUBSTRING({packed}, NULLIF(CHARINDEX('|' + {hex} + ':', {packed}), 0) + DATALENGTH({hex}) + 2, {IndexDigits.ToString(CultureInfo.InvariantCulture)}) AS int) END");
            chunk.Clear().Append('|');
            first = null;
        }

        foreach (var (key, keyIndex) in keys.Where(pair => pair.Key.Length <= PackedKeyUnits).Select(pair => (SqlDigest.Utf16Hex(pair.Key), pair.Value)).OrderBy(pair => pair.Item1, StringComparer.Ordinal))
        {
            var item = string.Create(CultureInfo.InvariantCulture, $"{key}:{keyIndex.ToString(new string('0', IndexDigits), CultureInfo.InvariantCulture)}");
            if (chunk.Length + item.Length + 1 > ChunkLength)
            {
                Flush();
            }

            first ??= key;
            last = key;
            chunk.Append(item).Append('|');
        }

        Flush();
        foreach (var (key, keyIndex) in keys.Where(pair => pair.Key.Length > PackedKeyUnits))
        {
            lookups.Add($"CASE WHEN {KeyMatches(row, Text(key))} THEN {keyIndex.ToString(CultureInfo.InvariantCulture)} END");
        }

        var column = Alias();
        var lookup = lookups.Count == 1 ? lookups[0] : $"COALESCE({string.Join(", ", lookups)})";
        from.Append(CultureInfo.InvariantCulture, $" OUTER APPLY OPENJSON(JSON_ARRAY({lookup})) WITH (i int '$') AS {column}");
        return $"{column}.i";
    }

    /// <summary>The longest key packed by <see cref="KeyIndex"/>: its hex, a colon and the index fit one chunk.</summary>
    private const int PackedKeyUnits = (ChunkLength - 2 - 1 - IndexDigits) / 4;

    /// <summary>The digits of a key's index in a <see cref="KeyIndex"/> entry.</summary>
    private const int IndexDigits = 5;
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
    /// condition (a range such as <c>{"gte": 1, "lt": 2}</c> is two conditions). Unset and <c>ne</c> conditions merge too,
    /// on any paths and without that assumption: <c>NOT EXISTS(a) AND NOT EXISTS(b)</c> is <c>NOT (EXISTS(a) OR EXISTS(b))</c>,
    /// one <see cref="AnyExists"/>; on one path their equalities become one list match.
    /// </summary>
    private IEnumerable<string> Conjuncts(IReadOnlyList<FilterNode> children)
    {
        var groups = SamePath(children, IsPositive, minimum: 2);
        var negatives = SamePath(children, op => !IsPositive(op), minimum: 1);
        if (negatives.Count == 1 && negatives[0].Count == 1)
        {
            negatives.Clear();
        }

        var merged = groups.Concat(negatives).SelectMany(group => group).ToHashSet<FilterNode>(ReferenceEqualityComparer.Instance);

        foreach (var group in groups)
        {
            yield return MetadataExists(group[0].Path, row => string.Join(" AND ", group.Select(path => PositiveCondition(row, path))));
        }

        if (negatives.Count > 0)
        {
            yield return $"(NOT {AnyExists([.. negatives.Select(group => new FlagMember(group[0].Path, row => AnyOf(row, group)))])})";
        }

        foreach (var child in children.Where(child => !merged.Contains(child)))
        {
            yield return Write(child);
        }
    }

    /// <summary>
    /// The children of an OR (or NOT), with every positive metadata condition merged into one <see cref="AnyExists"/>:
    /// <c>EXISTS(a) OR EXISTS(b)</c> is one existence test, and on one path <c>EXISTS(p, a OR b)</c>. Equalities on one path
    /// become one list match: the parser turns a metadata <c>in</c> into such equalities, and a packed list keeps a
    /// 1,000-value list cheap.
    /// </summary>
    private IEnumerable<string> Alternatives(IReadOnlyList<FilterNode> children)
    {
        var groups = SamePath(children, IsPositive, minimum: 1);
        if (groups.Count == 1 && groups[0].Count == 1)
        {
            groups.Clear();
        }

        var merged = groups.SelectMany(group => group).ToHashSet<FilterNode>(ReferenceEqualityComparer.Instance);
        if (groups.Count > 0)
        {
            yield return AnyExists([.. groups.Select(group => new FlagMember(group[0].Path, row => AnyOf(row, group)))]);
        }

        foreach (var child in children.Where(child => !merged.Contains(child)))
        {
            yield return Write(child);
        }
    }

    /// <summary>The reachable metadata conditions among <paramref name="children"/> whose operator passes <paramref name="op"/>, grouped by path, in groups of at least <paramref name="minimum"/>.</summary>
    private static List<List<FilterNode.MetadataPath>> SamePath(IReadOnlyList<FilterNode> children, Func<FilterOp, bool> op, int minimum) =>
    [
        .. children
            .OfType<FilterNode.MetadataPath>()
            .Where(path => op(path.Op) && Reachable(path.Path))
            .GroupBy(path => new JsonArray([.. path.Path.Select(key => (JsonNode)key)]).ToJsonString(), StringComparer.Ordinal)
            .Select(group => group.ToList())
            .Where(group => group.Count >= minimum),
    ];

    /// <summary>
    /// The value at the row satisfies one of <paramref name="paths"/> (all on that path): a positive operator as itself,
    /// <c>ne</c> as the equality it negates and "unset" as "set" (for a caller that negates the whole). Equalities become one
    /// list match.
    /// </summary>
    private string AnyOf(MetadataRow row, IReadOnlyList<FilterNode.MetadataPath> paths)
    {
        // By reference: records compare their JSON operands by value, which can throw for an extreme exponent.
        var equalities = paths.Where(path => path.Op is FilterOp.Eq or FilterOp.Ne).ToHashSet<FilterNode.MetadataPath>(ReferenceEqualityComparer.Instance);
        var parts = new List<string>();
        if (equalities.Count > 1)
        {
            parts.Add(ListMatch(row, [.. paths.Where(equalities.Contains).Select(path => path.Value!)]));
        }

        foreach (var path in paths.Where(path => equalities.Count <= 1 || !equalities.Contains(path)))
        {
            parts.Add(path.Op switch
            {
                FilterOp.Ne => ScalarEquals(row, path.Value!),
                FilterOp.IsNull => $"{row.Type} <> 0",
                _ => PositiveCondition(row, path),
            });
        }

        return $"({string.Join(" OR ", parts)})";
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

    /// <summary>
    /// Ordinal substring (or, with <paramref name="fold"/>, <c>UPPER</c>-folded substring) of non-null text: <c>CHARINDEX</c>
    /// under a binary collation, which compares code units exactly (trailing spaces, NUL and surrogates included) and, unlike
    /// <c>LIKE '%…%'</c>, does not slow down quadratically on repetitive text (partner review I3: a 1990-character operand
    /// over 10,000 rows of <c>'s'</c> x 1990 took 81 s with <c>LIKE</c>, 33 ms with <c>CHARINDEX</c>). Text shorter than the
    /// operand is ruled out by its length first (<c>CASE</c> fixes the order, and <c>UPPER</c> keeps lengths). Every string
    /// contains the empty string, which <c>CHARINDEX</c> would not find.
    /// </summary>
    private string Contains(string text, string operand, bool fold)
    {
        if (operand.Length == 0)
        {
            return True;
        }

        var longEnough = $"DATALENGTH({text}) >= {(2L * operand.Length).ToString(CultureInfo.InvariantCulture)}";
        if (operand.Length <= MaxCharIndexOperand)
        {
            return $"(CASE WHEN {longEnough} THEN CASE WHEN {Find(text, Text(operand), fold)} > 0 THEN 1 ELSE 0 END ELSE 0 END = 1)";
        }

        // Too long for CHARINDEX to search for. Finding the longest prefix it can search for picks the candidates cheaply
        // (containing the operand implies containing its prefix, folded or not); only those are scanned exactly, comparing
        // the operand with every substring of its length, positions numbered by OPENJSON over an array of that many
        // elements. CASE fixes the order of evaluation, so the scan never runs for a row the length or prefix rules out.
        var prefix = operand[..(char.IsHighSurrogate(operand[MaxCharIndexOperand - 1]) ? MaxCharIndexOperand - 1 : MaxCharIndexOperand)];
        var candidate = $"{Find(text, Text(prefix), fold)} > 0";
        var value = Text(operand);
        var length = Parameter(SqlDbType.Int, operand.Length.ToString(CultureInfo.InvariantCulture), (name, _) => SqlParameters.Int(name, operand.Length));
        var position = Alias();
        var slice = $"SUBSTRING({text} COLLATE {Bin2}, CAST({position}.[key] AS int) + 1, {length})";
        var match = fold ? $"UPPER({slice}) = UPPER({value} COLLATE {Bin2})" : $"{slice} = {value} COLLATE {Bin2}";
        var scan = $"EXISTS (SELECT 1 FROM OPENJSON(N'[' + REPLICATE(CAST(N'0,' AS nvarchar(max)), DATALENGTH({text}) / 2 - {length}) + N'0]') AS {position} WHERE {match})";
        return $"(CASE WHEN {longEnough} THEN CASE WHEN {candidate} THEN CASE WHEN {scan} THEN 1 ELSE 0 END ELSE 0 END ELSE 0 END = 1)";
    }

    /// <summary>The 1-based position of <paramref name="operand"/> in <paramref name="text"/> (0 when absent), code unit for code unit.</summary>
    private static string Find(string text, string operand, bool fold) => fold
        ? $"CHARINDEX(UPPER({operand} COLLATE {Bin2}), UPPER({text} COLLATE {Bin2}))"
        : $"CHARINDEX({operand} COLLATE {Bin2}, {text} COLLATE {Bin2})";
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
    /// <param name="gate">
    /// For a row source shared by several keys (<see cref="WriteWithFlags"/>), the condition that the row is under one of
    /// the keys whose conditions use it: derived columns are computed only for such rows.
    /// </param>
    private sealed class MetadataRow(FilterWriter writer, string row, StringBuilder from, string? gate = null)
    {
        private string? _numberKey;
        private string? _numberDigest;
        private string? _stringDigest;

        public string Value => $"{row}.[value]";

        public string Type => $"{row}.[type]";

        /// <summary>The value's number key, joined into the row's FROM clause the first time it is needed (once per row).</summary>
        public string NumberKey => _numberKey ??= writer.NumberKey(Value, Gated($"{Type} = 2"), from);

        /// <summary>
        /// The <c>len:SHA256</c> entry (<see cref="SqlDigest.KeySql"/>) of a number key longer than <see cref="ShortKey"/>
        /// (else NULL), computed once per row.
        /// </summary>
        public string NumberDigest => _numberDigest ??= writer.Digest(
            $"CASE WHEN LEN({NumberKey}) > {ShortKey.ToString(CultureInfo.InvariantCulture)} THEN {SqlDigest.KeySql(NumberKey, writer.DigestHexLength)} END", from);

        /// <summary>
        /// The <c>len:SHA256</c> entry (<see cref="SqlDigest.StringSql"/>) of a string longer than <see cref="RawString"/>
        /// code units (else NULL), computed once per row.
        /// </summary>
        public string StringDigest => _stringDigest ??= writer.Digest(
            $"CASE WHEN {Gated($"{Type} = 1 AND DATALENGTH({Value}) > {(RawString * 2).ToString(CultureInfo.InvariantCulture)}")} THEN {SqlDigest.StringSql(Value, writer.DigestHexLength)} END", from);

        /// <summary>The number keys of the elements of <paramref name="array"/>, joined into the row's FROM clause.</summary>
        public string ArrayNumberKeys(string array) => writer.ArrayNumberKeys(gate is null ? array : $"CASE WHEN {gate} THEN {array} END", from);

        private string? _arrayStrings;
        private string? _foldedArrayStrings;

        /// <summary>
        /// For an array value, its string elements as <c>|hex|hex|…|</c> (UTF-16LE code units, upper-cased first when
        /// <paramref name="fold"/>), NULL otherwise or when it has none; joined into the row's FROM clause once per row.
        /// </summary>
        public string ArrayStrings(bool fold) => fold
            ? _foldedArrayStrings ??= writer.ArrayStrings(Value, Gated($"{Type} = 4"), fold: true, from)
            : _arrayStrings ??= writer.ArrayStrings(Value, Gated($"{Type} = 4"), fold: false, from);

        private string Gated(string condition) => gate is null ? condition : $"{condition} AND ({gate})";
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
            FilterOp.IsNull => $"(NOT {SharedExists(path, "set", row => $"{row.Type} <> 0")})",
            FilterOp.NotNull => SharedExists(path, "set", row => $"{row.Type} <> 0"),
            FilterOp.Ne => $"(NOT {SharedExists(path, "eq", row => ScalarEquals(row, operand!))})",
            FilterOp.Eq => SharedExists(path, "eq", row => ScalarEquals(row, operand!)),
            _ => SharedExists(path, path.Op.ToString(), row => PositiveCondition(row, path)),
        };
    }

    /// <summary>While flags are collected, the flag already made for an identical condition (path, test and operand text).</summary>
    private readonly Dictionary<string, string> _sharedFlags = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="MetadataExists(IReadOnlyList{string}, Func{MetadataRow, string})"/> of one condition; while flags are
    /// collected, identical conditions anywhere in the filter (such as one repeated in every branch of an OR) share one
    /// flag. <paramref name="test"/> names the condition so that <c>eq</c>/<c>ne</c> and set/unset share theirs.
    /// </summary>
    private string SharedExists(FilterNode.MetadataPath path, string test, Func<MetadataRow, string> condition)
    {
        if (_flags is null)
        {
            return MetadataExists(path.Path, condition);
        }

        var identity = string.Concat(test, "|", new JsonArray([.. path.Path.Select(key => (JsonNode)key)]).ToJsonString(), "|", path.Value?.ToJsonString());
        if (!_sharedFlags.TryGetValue(identity, out var flag))
        {
            flag = MetadataExists(path.Path, condition);
            _sharedFlags.Add(identity, flag);
        }

        return flag;
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
        return AnyExists([new FlagMember(path, condition)]);
    }

    /// <summary>
    /// Some entry satisfies one of <paramref name="members"/> (the OR of their <see cref="MetadataExists(IReadOnlyList{string}, Func{MetadataRow, string})"/>):
    /// while <see cref="WriteFilter"/> collects flags, one flag for all of them; otherwise one <c>EXISTS</c> each.
    /// </summary>
    private string AnyExists(IReadOnlyList<FlagMember> members)
    {
        if (_flags is not null)
        {
            // A flag tests its members in one CASE, whose compile time grows faster than linearly with its branches, so
            // a large OR is split into several flags of at most FlagMembers members each.
            var references = new List<string>();
            foreach (var chunk in members.Chunk(FlagMembers))
            {
                _flags.Add(new MetadataFlag(chunk));
                references.Add($"({_flagsAlias}.l{(_flags.Count - 1).ToString(CultureInfo.InvariantCulture)} = 1)");
            }

            return references.Count == 1 ? references[0] : $"({string.Join(" OR ", references)})";
        }

        var exists = members.Select(member => MetadataExists(member.Path, member.Condition, $"{table}.Metadata")).ToList();
        return exists.Count == 1 ? exists[0] : $"({string.Join(" OR ", exists)})";
    }

    private string MetadataExists(IReadOnlyList<string> path, Func<MetadataRow, string> condition, string source)
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
                ? $"FROM OPENJSON({source}) AS {step}"
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

    /// <summary>
    /// A string containing the text, or an array with a string element equal to it (both under the same folding). The
    /// array test is a <c>CHARINDEX</c> of the operand's delimited hex in the row's <see cref="MetadataRow.ArrayStrings"/>,
    /// not a subquery per condition: hex holds no <c>|</c>, so a match between delimiters is a whole element, equal code
    /// unit for code unit (trailing spaces included). Folded, both sides are <c>UPPER</c>-ed under the binary collation
    /// first, which keeps their lengths. An operand whose delimited hex is longer than <c>CHARINDEX</c> may search for
    /// (8000 characters, so over 1999 code units) is compared with the array's elements by a subquery instead.
    /// </summary>
    private string MetadataContains(MetadataRow row, string operand, bool fold)
    {
        if (operand.Length > (ChunkLength - 2) / 4)
        {
            var element = Alias();
            var value = Text(operand);
            var equal = fold
                ? $"UPPER({element}.[value] COLLATE {Bin2}) = UPPER({value} COLLATE {Bin2})"
                : $"{element}.[value] COLLATE {Bin2} = {value}";
            return $"(({row.Type} = 1 AND {Contains(row.Value, operand, fold)}) OR ({row.Type} = 4 AND EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN {row.Type} = 4 THEN {row.Value} END) AS {element} WHERE {element}.[type] = 1 AND {equal} AND DATALENGTH({element}.[value]) = DATALENGTH({value}))))";
        }

        var wanted = fold
            ? $"CONVERT(varchar(max), CAST(UPPER({Text(operand)} COLLATE {Bin2}) AS varbinary(max)), 2)"
            : Parameter(SqlDbType.VarChar, SqlDigest.Utf16Hex(operand), SqlParameters.Ascii);
        return $"(({row.Type} = 1 AND {Contains(row.Value, operand, fold)}) OR ({row.Type} = 4 AND CHARINDEX('|' + {wanted} + '|' COLLATE {Bin2}, {row.ArrayStrings(fold)}) > 0))";
    }

    /// <summary>Joins <see cref="MetadataRow.ArrayStrings"/> into <paramref name="from"/>: one aggregate per row, over arrays only.</summary>
    private string ArrayStrings(string value, string when, bool fold, StringBuilder from)
    {
        var strings = Alias();
        var element = Alias();
        var text = fold ? $"UPPER({element}.[value] COLLATE {Bin2})" : $"{element}.[value]";
        from.Append(CultureInfo.InvariantCulture, $" OUTER APPLY (SELECT '|' + STRING_AGG(CONVERT(varchar(max), CAST({text} AS varbinary(max)), 2), '|') + '|' AS s FROM OPENJSON(CASE WHEN {when} THEN {value} END) AS {element} WHERE {element}.[type] = 1) AS {strings}");
        return $"{strings}.s";
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
    /// The value equals (same kind) some operand. Operands are split by kind: strings are tested with
    /// <see cref="StringIn"/>, booleans as fixed literals, and numbers as a set of order keys tested against the value's
    /// key, which is computed once per row.
    /// </summary>
    private string ListMatch(MetadataRow row, IReadOnlyList<JsonNode> operands)
    {
        var parts = new List<string>();
        var strings = operands.Where(o => o.GetValueKind() == JsonValueKind.String).Select(o => o.GetValue<string>()).ToList();
        if (strings.Count > 0)
        {
            parts.Add($"({row.Type} = 1 AND {StringIn(row, strings)})");
        }

        foreach (var flag in operands.Select(o => o.GetValueKind()).Where(k => k is JsonValueKind.True or JsonValueKind.False).Distinct())
        {
            parts.Add($"({row.Type} = 3 AND {row.Value} = {(flag == JsonValueKind.True ? "N'true'" : "N'false'")})");
        }

        var numbers = NumberKeys(operands);
        if (numbers.Count > 0)
        {
            parts.Add($"({row.Type} = 2 AND {KeyIn(row, numbers)})");
        }

        return parts.Count == 0 ? False : $"({string.Join(" OR ", parts)})";
    }

    /// <summary>
    /// The longest string, in UTF-16 code units, packed exactly as its own hex (4 characters per unit) rather than as a
    /// <c>len:SHA256</c> entry: such an entry is shorter than a digest entry.
    /// </summary>
    private const int RawString = 16;

    /// <summary>
    /// Whether the string value of <paramref name="row"/> is exactly (code unit for code unit, trailing spaces included)
    /// one of <paramref name="strings"/>, without parsing a list per row and at a per-row cost independent of the
    /// operands' number and length. Strings of up to <see cref="RawString"/> code units are packed (<see cref="PackedIn"/>)
    /// as the hex of their UTF-16LE code units, exactly. Longer ones go through <see cref="VerifiedIn"/>: a prefilter on
    /// the row's code-unit count and SHA-256 (<see cref="MetadataRow.StringDigest"/>, computed once per row, string rows
    /// only), confirmed by comparing the row's UTF-16LE bytes with the one operand the entry selects. The two tiers apply
    /// to disjoint rows (by the value's length), so a raw entry is never compared with a digest entry.
    /// </summary>
    private string StringIn(MetadataRow row, IReadOnlyList<string> strings)
    {
        var rawBytes = (RawString * 2).ToString(CultureInfo.InvariantCulture);
        var distinct = strings.Distinct(StringComparer.Ordinal).ToList();
        var parts = PackedIn(
            $"CASE WHEN DATALENGTH({row.Value}) <= {rawBytes} THEN CONVERT(varchar({(RawString * 4).ToString(CultureInfo.InvariantCulture)}), CAST({row.Value} AS varbinary({rawBytes})), 2) END COLLATE {Bin2}",
            distinct.Where(s => s.Length <= RawString).Select(SqlDigest.Utf16Hex));
        if (distinct.Any(s => s.Length > RawString))
        {
            parts.AddRange(VerifiedIn(
                row.StringDigest,
                $"CAST({row.Value} AS varbinary(max))",
                [.. distinct.Where(s => s.Length > RawString).Select(s => (SqlDigest.OfString(s, digestHexLength), SqlDigest.Utf16Bytes(s)))]));
        }

        return $"({string.Join(" OR ", parts)})";
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

    /// <summary>
    /// The longest order key packed as itself in <see cref="KeyIn"/> (every number of up to about 85 significant digits);
    /// longer keys are packed as their length and SHA-256 digest.
    /// </summary>
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
    private string NumberKey(string value, string when, StringBuilder from)
    {
        var key = Alias();
        from.Append(CultureInfo.InvariantCulture, $" OUTER APPLY OPENJSON(JSON_ARRAY(dbo.JsonNumberOrderKey(CASE WHEN {when} THEN {value} END))) WITH (k varchar(max) '$') AS {key}");
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
    /// Whether the number value of <paramref name="row"/> is one of the numbers whose order keys are
    /// <paramref name="keys"/>, without parsing a list per row and at a per-row cost independent of the operands' number
    /// and length. Keys of up to <see cref="ShortKey"/> characters are packed (<see cref="PackedIn"/>) as themselves (they
    /// hold only digits and <c>:</c>), exactly. Longer ones go through <see cref="VerifiedIn"/>: a prefilter on the key's
    /// length and SHA-256 (<see cref="MetadataRow.NumberDigest"/>, computed once per row, number rows only), confirmed by
    /// comparing the whole key with the one operand key the entry selects. Keys are canonical, so equal keys mean equal
    /// numbers. The two tiers apply to disjoint rows (by the key's length).
    /// </summary>
    private string KeyIn(MetadataRow row, IReadOnlyList<string> keys)
    {
        var key = row.NumberKey;
        var parts = PackedIn(
            $"CASE WHEN LEN({key}) <= {ShortKey.ToString(CultureInfo.InvariantCulture)} THEN CAST({key} AS varchar({ShortKey.ToString(CultureInfo.InvariantCulture)})) END COLLATE {Bin2}",
            keys.Where(k => k.Length <= ShortKey));
        if (keys.Any(k => k.Length > ShortKey))
        {
            parts.AddRange(VerifiedIn(
                row.NumberDigest,
                $"CAST({key} AS varbinary(max))",
                [.. keys.Where(k => k.Length > ShortKey).Select(k => (SqlDigest.OfKey(k, digestHexLength), SqlDigest.KeyBytes(k)))]));
        }

        return $"({string.Join(" OR ", parts)})";
    }

    /// <summary>The digits of an operand's byte offset in a <see cref="VerifiedIn"/> entry.</summary>
    private const int OffsetDigits = 10;

    /// <summary>
    /// Whether the row, whose prefilter entry is <paramref name="entry"/> (NULL for a row that cannot match) and whose
    /// value's bytes are <paramref name="valueBytes"/>, is exactly one of <paramref name="operands"/> (each a prefilter
    /// entry and the operand's bytes). Membership uses a SHA-256 + kind + length prefilter, confirmed by exact comparison.
    /// </summary>
    /// <remarks>
    /// The operands' bytes are concatenated into one <c>varbinary(max)</c> parameter. Each operand is packed as
    /// <c>len:HEX:offset</c> (offset: <see cref="OffsetDigits"/> digits, 1-based, into those bytes) into sorted
    /// <c>varchar(8000)</c> chunks passed with their first and last entry, as in <see cref="PackedIn"/>. A row scans only
    /// the chunk whose range holds its entry, with one <c>CHARINDEX</c>. Only when it finds the entry there (otherwise its
    /// position is NULL and so is everything derived from it), the offset beside it selects the operand, and the row's bytes
    /// are compared with that operand's bytes: the exact comparison runs once per prefilter hit and never rescans the operands. Lengths are equal by then
    /// (the entry holds the length), so SQL's zero-padded <c>varbinary</c> comparison is exact. Operands sharing an entry
    /// (a digest collision; only forced ones in tests) cannot be told apart by position, so each such group is tested
    /// against all its members' bytes.
    /// </remarks>
    private List<string> VerifiedIn(string entry, string valueBytes, IReadOnlyList<(string Entry, byte[] Bytes)> operands)
    {
        using var buffer = new MemoryStream();
        long Append(byte[] bytes)
        {
            var offset = buffer.Position + 1;
            buffer.Write(bytes);
            return offset;
        }

        var singles = new List<(string Entry, long Offset)>();
        var collisions = new List<(string Entry, List<long> Offsets, int Length)>();
        foreach (var group in operands.GroupBy(o => o.Entry, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                singles.Add((group.Key, Append(members[0].Bytes)));
            }
            else
            {
                collisions.Add((group.Key, [.. members.Select(m => Append(m.Bytes))], members[0].Bytes.Length));
            }
        }

        var bytes = Bytes(buffer.ToArray());
        var parts = new List<string>();
        var needle = $"'|' + {entry} + ':'";
        var chunk = new StringBuilder("|");
        string? first = null;
        string? last = null;

        void Flush()
        {
            if (first is null)
            {
                return;
            }

            var low = Parameter(SqlDbType.VarChar, first, SqlParameters.Ascii);
            var high = Parameter(SqlDbType.VarChar, last!, SqlParameters.Ascii);
            var packed = Parameter(SqlDbType.VarChar, chunk.ToString(), SqlParameters.Ascii);
            // One CHARINDEX: a miss is a NULL position, so the offset, the slice and the comparison are NULL (no match).
            var offset = $"CAST(SUBSTRING({packed}, NULLIF(CHARINDEX({needle}, {packed}), 0) + DATALENGTH({entry}) + 2, {OffsetDigits.ToString(CultureInfo.InvariantCulture)}) AS bigint)";
            parts.Add(
                $"(CASE WHEN {entry} >= {low} AND {entry} <= {high} THEN " +
                $"CASE WHEN SUBSTRING({bytes}, {offset}, DATALENGTH({valueBytes})) = {valueBytes} THEN 1 ELSE 0 END " +
                "ELSE 0 END = 1)");
            chunk.Clear().Append('|');
            first = null;
        }

        foreach (var (key, offset) in singles)
        {
            var item = string.Create(CultureInfo.InvariantCulture, $"{key}:{offset.ToString(new string('0', OffsetDigits), CultureInfo.InvariantCulture)}");
            if (chunk.Length + item.Length + 1 > ChunkLength)
            {
                Flush();
            }

            first ??= key;
            last = key;
            chunk.Append(item).Append('|');
        }

        Flush();

        foreach (var (key, offsets, length) in collisions)
        {
            var candidates = string.Join(", ", offsets.Select(o => string.Create(CultureInfo.InvariantCulture, $"SUBSTRING({bytes}, {o}, {length})")));
            parts.Add($"(CASE WHEN {entry} = {Parameter(SqlDbType.VarChar, key, SqlParameters.Ascii)} THEN CASE WHEN {valueBytes} IN ({candidates}) THEN 1 ELSE 0 END ELSE 0 END = 1)");
        }

        return parts;
    }

    /// <summary>
    /// Appends to <paramref name="from"/> the value of <paramref name="expression"/> (a digest entry, or NULL) and returns it as
    /// a column, evaluated once per row: the argument of a table-valued function, like <see cref="NumberKey"/>, rather than
    /// an expression the optimizer copies into every reference.
    /// </summary>
    private string Digest(string expression, StringBuilder from)
    {
        var digest = Alias();
        from.Append(CultureInfo.InvariantCulture, $" OUTER APPLY OPENJSON(JSON_ARRAY({expression})) WITH (d varchar(100) '$') AS {digest}");
        return $"{digest}.d COLLATE {Bin2}";
    }

    /// <summary>
    /// Whether the per-row <paramref name="entry"/> (NULL for a row that cannot match) is one of
    /// <paramref name="entries"/>, which must not contain <c>|</c> and are at most a few hundred characters. The entries
    /// are sorted (ordinal, which is binary-collation order for them) and packed as <c>|e1|e2|…|</c> into
    /// <c>varchar(8000)</c> chunks, each passed with its first and last entry. A row scans only the chunk whose range holds
    /// its entry (at most one, since the ranges are disjoint), so its cost does not grow with the list: one
    /// <c>CHARINDEX</c> over at most 8000 characters plus two comparisons per chunk.
    /// </summary>
    private List<string> PackedIn(string entry, IEnumerable<string> entries)
    {
        var parts = new List<string>();
        var chunk = new StringBuilder("|");
        string? first = null;
        string? last = null;

        void Flush()
        {
            if (first is null)
            {
                return;
            }

            var low = Parameter(SqlDbType.VarChar, first, SqlParameters.Ascii);
            var high = Parameter(SqlDbType.VarChar, last!, SqlParameters.Ascii);
            var packed = Parameter(SqlDbType.VarChar, chunk.ToString(), SqlParameters.Ascii);
            parts.Add($"(CASE WHEN {entry} >= {low} AND {entry} <= {high} THEN CHARINDEX('|' + {entry} + '|', {packed}) END > 0)");
            chunk.Clear().Append('|');
            first = null;
        }

        foreach (var value in entries.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (chunk.Length + value.Length + 1 > ChunkLength)
            {
                Flush();
            }

            first ??= value;
            last = value;
            chunk.Append(value).Append('|');
        }

        Flush();
        return parts;
    }
    // ------------------------------------------------------------------------------------------------ names

    private string Alias() => $"fa{_aliases++}";

    private string Text(string value) => Parameter(SqlDbType.NVarChar, value, SqlParameters.Text);

    /// <summary>A new <c>varbinary(max)</c> parameter holding <paramref name="value"/>.</summary>
    private string Bytes(byte[] value)
    {
        var name = $"@f{_parameters.Count}";
        _parameters.Add(new SqlParameter(name, SqlDbType.VarBinary, -1) { Value = value });
        return name;
    }

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
