using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.DataLayer.InMemory;

/// <summary>
/// Evaluates a parsed <see cref="FilterNode"/> against stored rows, implementing exactly the semantics documented on
/// <see cref="FilterNode"/>, <see cref="FilterOp"/> and <see cref="FilterColumns"/> (pinned by the shared
/// <c>filter-cases.json</c>). A null filter matches every row.
/// </summary>
/// <remarks>
/// Operands are trusted to be in the parser's normalized form (see <see cref="FilterNode.Field.Value"/>). Every column
/// of this provider is always set, except a session's <see cref="FilterColumns.PeerId"/>, which is an existential
/// predicate over its active members. Store list calls <see cref="Prepare"/> once and match every row against the result.
/// </remarks>
internal static class InMemoryFilterEvaluator
{
    /// <summary>
    /// Compiles <paramref name="filter"/> once, reading every node's column, operator, operand and path exactly once;
    /// null for a null filter (which matches every row). Call it once per query, before the per-row loop and outside
    /// any lock: it reads the caller's tree, which must never happen under a gate.
    /// </summary>
    /// <remarks>
    /// Nothing is validated against a record kind here: an unknown column throws when a row first reaches its node, so
    /// an empty row set never throws.
    /// </remarks>
    public static PreparedFilter? Prepare(FilterNode? filter)
    {
        if (filter is null)
        {
            return null;
        }

        var reads = 0;
        var predicate = Compile(filter, ref reads);
        return new PreparedFilter(predicate, reads);
    }

    public static bool Matches(FilterNode? filter, WorkspaceRecord workspace) => Matches(Prepare(filter), workspace);

    public static bool Matches(FilterNode? filter, PeerRecord peer) => Matches(Prepare(filter), peer);

    /// <param name="filter">The filter.</param>
    /// <param name="session">The session.</param>
    /// <param name="activeMembers">The peer names of the session's active members (members who left excluded).</param>
    public static bool Matches(FilterNode? filter, SessionRecord session, IReadOnlyCollection<string> activeMembers) =>
        Matches(Prepare(filter), session, activeMembers);

    public static bool Matches(FilterNode? filter, MessageRecord message) => Matches(Prepare(filter), message);

    public static bool Matches(PreparedFilter? filter, WorkspaceRecord workspace) =>
        filter is null || filter.Matches(new Row(workspace.Metadata, column => column switch
        {
            FilterColumns.Name => workspace.Name,
            FilterColumns.CreatedAt => workspace.CreatedAt,
            _ => throw UnknownColumn(column, ResourceKind.Workspace),
        }));

    public static bool Matches(PreparedFilter? filter, PeerRecord peer) =>
        filter is null || filter.Matches(new Row(peer.Metadata, column => column switch
        {
            FilterColumns.Name => peer.Name,
            FilterColumns.CreatedAt => peer.CreatedAt,
            _ => throw UnknownColumn(column, ResourceKind.Peer),
        }));

    /// <param name="filter">The prepared filter.</param>
    /// <param name="session">The session.</param>
    /// <param name="activeMembers">The peer names of the session's active members (members who left excluded).</param>
    public static bool Matches(PreparedFilter? filter, SessionRecord session, IReadOnlyCollection<string> activeMembers) =>
        filter is null || filter.Matches(new Row(session.Metadata, column => column switch
        {
            FilterColumns.Name => session.Name,
            FilterColumns.CreatedAt => session.CreatedAt,
            FilterColumns.IsActive => session.State == LifecycleState.Active,
            FilterColumns.PeerId => new AnyOf(activeMembers),
            _ => throw UnknownColumn(column, ResourceKind.Session),
        }));

    public static bool Matches(PreparedFilter? filter, MessageRecord message) =>
        filter is null || filter.Matches(new Row(message.Metadata, column => column switch
        {
            FilterColumns.PublicId => message.PublicId,
            FilterColumns.SessionId => message.SessionName,
            FilterColumns.PeerId => message.PeerName,
            FilterColumns.Content => message.Content,
            FilterColumns.TokenCount => (decimal)message.TokenCount,
            FilterColumns.CreatedAt => message.CreatedAt,
            _ => throw UnknownColumn(column, ResourceKind.Message),
        }));

    /// <summary>
    /// A row as the evaluator sees it. A column value is a <see cref="string"/>, <see cref="decimal"/>,
    /// <see cref="DateTimeOffset"/>, <see cref="bool"/>, or an <see cref="AnyOf"/> for an existential column.
    /// </summary>
    internal sealed record Row(JsonObject Metadata, Func<string, object> Column);

    /// <summary>A multi-valued text column that matches when any of its values does.</summary>
    private sealed record AnyOf(IReadOnlyCollection<string> Values);

    /// <summary>
    /// Walks the tree once. Each leaf's operand is the single <c>Value</c> read, captured by its closure; the children of
    /// a combinator are enumerated once. <paramref name="reads"/> counts the operand reads.
    /// </summary>
    private static Func<Row, bool> Compile(FilterNode node, ref int reads)
    {
        switch (node)
        {
            case FilterNode.And and:
            {
                var children = CompileAll(and.Children, ref reads);
                return row =>
                {
                    foreach (var child in children)
                    {
                        if (!child(row))
                        {
                            return false;
                        }
                    }

                    return true;
                };
            }

            case FilterNode.Or or:
            {
                var children = CompileAll(or.Children, ref reads);
                return row => AnyMatches(children, row);
            }

            case FilterNode.Not not:
            {
                var children = CompileAll(not.Children, ref reads);
                return row => !AnyMatches(children, row);
            }

            case FilterNode.MatchAll:
                return static _ => true;

            case FilterNode.MatchNone:
                return static _ => false;

            case FilterNode.Field field:
            {
                var column = field.Column;
                var op = field.Op;
                var operand = field.Value;
                reads++;
                return row => row.Column(column) switch
                {
                    AnyOf anyOf => EvaluateAnyOf(anyOf, op, operand),
                    var value => EvaluateColumn(value, op, operand),
                };
            }

            case FilterNode.MetadataPath path:
            {
                var keys = path.Path;
                var op = path.Op;
                var operand = path.Value;
                reads++;
                return row => EvaluateMetadata(Resolve(row.Metadata, keys), op, operand);
            }

            default:
                // Unreachable for the closed hierarchy except a null child of a hand-built combinator, which has always
                // failed when a row reaches it, not before; keep that timing.
                return _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown filter node.");
        }
    }

    private static Func<Row, bool>[] CompileAll(IReadOnlyList<FilterNode> nodes, ref int reads)
    {
        var compiled = new Func<Row, bool>[nodes.Count];
        for (var i = 0; i < compiled.Length; i++)
        {
            compiled[i] = Compile(nodes[i], ref reads);
        }

        return compiled;
    }

    private static bool AnyMatches(Func<Row, bool>[] children, Row row)
    {
        foreach (var child in children)
        {
            if (child(row))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------------------------------------ columns

    /// <summary>
    /// Existential semantics: <c>ne</c> is the negation of <c>eq</c> (so it includes rows with no values), the null
    /// checks test for emptiness, and every other operator matches when any value matches.
    /// </summary>
    private static bool EvaluateAnyOf(AnyOf column, FilterOp op, JsonNode? operand) => op switch
    {
        FilterOp.IsNull => column.Values.Count == 0,
        FilterOp.NotNull => column.Values.Count > 0,
        FilterOp.Ne => !column.Values.Any(value => EvaluateColumn(value, FilterOp.Eq, operand)),
        _ => column.Values.Any(value => EvaluateColumn(value, op, operand)),
    };

    /// <summary>A comparison on a set (never null) single-valued column.</summary>
    private static bool EvaluateColumn(object value, FilterOp op, JsonNode? operand) => op switch
    {
        FilterOp.IsNull => false,
        FilterOp.NotNull => true,
        FilterOp.Eq => CompareColumn(value, operand!) == 0,
        FilterOp.Ne => CompareColumn(value, operand!) != 0,
        FilterOp.Gt => CompareColumn(value, operand!) > 0,
        FilterOp.Gte => CompareColumn(value, operand!) >= 0,
        FilterOp.Lt => CompareColumn(value, operand!) < 0,
        FilterOp.Lte => CompareColumn(value, operand!) <= 0,
        FilterOp.In => operand!.AsArray().Any(element => CompareColumn(value, element!) == 0),
        FilterOp.Contains => ((string)value).Contains(operand!.GetValue<string>(), StringComparison.Ordinal),
        FilterOp.IContains => Fold((string)value).Contains(Fold(operand!.GetValue<string>()), StringComparison.Ordinal),
        _ => throw new NotSupportedException($"Operator {op} does not apply to a column."),
    };

    /// <summary>Compares a column value with a normalized operand of the column's type; text is ordinal.</summary>
    private static int CompareColumn(object value, JsonNode operand) => value switch
    {
        string text => string.CompareOrdinal(text, operand.GetValue<string>()),
        decimal number => number.CompareTo(operand.GetValue<decimal>()),
        DateTimeOffset instant => instant.CompareTo(operand.GetValue<DateTimeOffset>()),
        bool flag => flag.CompareTo(operand.GetValue<bool>()),
        _ => throw new NotSupportedException($"Unsupported column value type {value.GetType()}."),
    };

    private static NotSupportedException UnknownColumn(string column, ResourceKind kind) =>
        new($"Column '{column}' is not filterable on a {kind}.");

    // ------------------------------------------------------------------------------------------------ metadata

    /// <summary>The value at the path, or null when it is unset (a key is missing, holds JSON null, or a step is not an object).</summary>
    private static JsonNode? Resolve(JsonObject metadata, IReadOnlyList<string> path)
    {
        JsonNode? current = metadata;
        foreach (var key in path)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(key, out current))
            {
                return null;
            }
        }

        return current;
    }

    /// <remarks>
    /// <see cref="CompareSameKind"/> returns null across kinds, and C#'s lifted comparisons make that false for
    /// <c>==</c> and the ordering operators but true for <c>!=</c>: exactly the documented kind rule.
    /// </remarks>
    private static bool EvaluateMetadata(JsonNode? value, FilterOp op, JsonNode? operand)
    {
        if (value is null)
        {
            // Unset: only the negative operators match.
            return op is FilterOp.IsNull or FilterOp.Ne;
        }

        return op switch
        {
            FilterOp.IsNull => false,
            FilterOp.NotNull => true,
            FilterOp.Eq => CompareSameKind(value, operand!) == 0,
            FilterOp.Ne => CompareSameKind(value, operand!) != 0,
            FilterOp.Gt => CompareSameKind(value, operand!) > 0,
            FilterOp.Gte => CompareSameKind(value, operand!) >= 0,
            FilterOp.Lt => CompareSameKind(value, operand!) < 0,
            FilterOp.Lte => CompareSameKind(value, operand!) <= 0,
            FilterOp.Contains => MetadataContains(value, operand!.GetValue<string>(), fold: false),
            FilterOp.IContains => MetadataContains(value, operand!.GetValue<string>(), fold: true),
            FilterOp.JsonContains => value is JsonArray array
                && operand!.AsArray().All(wanted => array.Any(element => CompareSameKind(element, wanted!) == 0)),
            _ => throw new NotSupportedException($"Operator {op} does not apply to metadata."),
        };
    }

    /// <summary>A string containing the text, or an array with a string element equal to it.</summary>
    private static bool MetadataContains(JsonNode value, string text, bool fold)
    {
        if (fold)
        {
            text = Fold(text);
        }

        return value switch
        {
            JsonArray array => array.Any(element =>
                AsString(element) is { } item && string.Equals(fold ? Fold(item) : item, text, StringComparison.Ordinal)),
            _ => AsString(value) is { } str && (fold ? Fold(str) : str).Contains(text, StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Compares two JSON scalars of the same kind (string ordinally, number numerically, boolean); null when the kinds
    /// differ or either side is not a scalar.
    /// </summary>
    private static int? CompareSameKind(JsonNode? value, JsonNode operand)
    {
        if (value is not JsonValue left || operand is not JsonValue right)
        {
            return null;
        }

        return (left.GetValueKind(), right.GetValueKind()) switch
        {
            (JsonValueKind.String, JsonValueKind.String) =>
                string.CompareOrdinal(left.GetValue<string>(), right.GetValue<string>()),
            (JsonValueKind.Number, JsonValueKind.Number) => CompareNumbers(left, right),
            (JsonValueKind.True or JsonValueKind.False, JsonValueKind.True or JsonValueKind.False) =>
                (left.GetValueKind() == JsonValueKind.True).CompareTo(right.GetValueKind() == JsonValueKind.True),
            _ => null,
        };
    }

    /// <summary>
    /// Compares by numeric value, using the JSON number text (stored JSON is canonical).
    /// </summary>
    /// <remarks>
    /// Exact: every metadata number comparison (<c>eq ne in gt gte lt lte</c> and array containment) compares the
    /// literals' values with no precision or range bound, so <c>0</c> and <c>1e-29</c> differ, as do integers beyond
    /// <see cref="decimal"/> and <see cref="double"/> precision; see <see cref="JsonNumberComparison"/>. The
    /// <c>token_count</c> column compares as <see cref="decimal"/>, its documented type.
    /// </remarks>
    private static int CompareNumbers(JsonValue left, JsonValue right) =>
        JsonNumberComparison.Compare(left.ToJsonString(), right.ToJsonString());

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Invariant simple uppercasing per character, the documented <c>icontains</c> folding.</summary>
    private static string Fold(string text) => text.ToUpperInvariant();
}
