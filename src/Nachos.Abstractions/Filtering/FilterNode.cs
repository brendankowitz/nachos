using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Json;

namespace Nachos.Abstractions.Filtering;

/// <summary>
/// Root of the list-filter syntax tree built by <see cref="FilterParser"/>. Store list methods accept an optional
/// node and the provider compiles it to its native query; a null filter means "no filter".
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy is closed: the subtypes below are the only ones. All are sealed with <b>structural</b> equality,
/// so two trees built from the same filter compare equal.
/// </para>
/// <para>
/// <b>Hand-built nodes are supported and validated.</b> A <see cref="Field"/> or <see cref="MetadataPath"/> checks its
/// parts when it is constructed (and again on a <c>with</c> expression): a column name or path key must be well-formed
/// UTF-16, and the operand must be strict JSON data (see <see cref="StrictJsonData"/>), or a
/// <see cref="NachosValidationException"/> is thrown. The operand is stored as a canonical copy, never the caller's
/// node, so no caller converter or serialization metadata can run when a provider evaluates, compares or prints the
/// node, and later changes to the caller's node or path list have no effect. The <c>Value</c> property returns a
/// detached copy of that stored operand each time it is read, so changing what it returns, or attaching it to another
/// tree, does not change the node; read it once and reuse it rather than once per row. Whether the operand suits the
/// column is not checked here: <see cref="FilterParser"/> normalizes it, and a hand-built node must follow the same
/// operand types.
/// </para>
/// <para>
/// <b>Semantics every provider must honour.</b> Positive conditions never match unset values;
/// <see cref="FilterOp.Ne"/> and <see cref="Not"/> do. See <see cref="FilterOp"/> for the operator rules and
/// <see cref="FilterColumns"/> for what each canonical column means.
/// </para>
/// </remarks>
public abstract record FilterNode
{
    private FilterNode()
    {
    }

    /// <summary>All children match. An empty list matches everything.</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "The node names are the documented AST vocabulary; consumers are C#.")]
    public sealed record And(IReadOnlyList<FilterNode> Children) : FilterNode
    {
        public bool Equals(And? other) => other is not null && SameChildren(Children, other.Children);

        protected override bool PrintMembers(StringBuilder builder) => PrintChildren(builder, Children);

        public override int GetHashCode() => HashChildren(Children);
    }

    /// <summary>At least one child matches. An empty list matches nothing.</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "The node names are the documented AST vocabulary; consumers are C#.")]
    public sealed record Or(IReadOnlyList<FilterNode> Children) : FilterNode
    {
        public bool Equals(Or? other) => other is not null && SameChildren(Children, other.Children);

        protected override bool PrintMembers(StringBuilder builder) => PrintChildren(builder, Children);

        public override int GetHashCode() => HashChildren(Children);
    }

    /// <summary>
    /// <c>NOT (c1 OR … OR cn)</c>: matches when <i>no</i> child matches. Rows whose field is unset are included.
    /// An empty list matches everything.
    /// </summary>
    [SuppressMessage("Naming", "CA1716", Justification = "The node names are the documented AST vocabulary; consumers are C#.")]
    public sealed record Not(IReadOnlyList<FilterNode> Children) : FilterNode
    {
        public bool Equals(Not? other) => other is not null && SameChildren(Children, other.Children);

        protected override bool PrintMembers(StringBuilder builder) => PrintChildren(builder, Children);

        public override int GetHashCode() => HashChildren(Children);
    }

    /// <summary>Matches every row.</summary>
    public sealed record MatchAll : FilterNode;

    /// <summary>Matches no row.</summary>
    public sealed record MatchNone : FilterNode;

    /// <summary>A comparison on a canonical column (see <see cref="FilterColumns"/>).</summary>
    /// <param name="Column">A <see cref="FilterColumns"/> constant.</param>
    /// <param name="Op">The comparison.</param>
    /// <param name="Value">
    /// The operand, already validated and normalized for the column's type: a string for text, a
    /// <see cref="decimal"/> with an integral value for <see cref="FilterColumns.TokenCount"/>, a <see cref="bool"/> for
    /// <see cref="FilterColumns.IsActive"/>, and a <see cref="DateTimeOffset"/> at UTC for
    /// <see cref="FilterColumns.CreatedAt"/> (read it with <c>Value.GetValue&lt;DateTimeOffset&gt;()</c>; a date-only
    /// input is UTC midnight). For <see cref="FilterOp.In"/> it is a non-empty <see cref="JsonArray"/> of such
    /// values (at most <see cref="FilterParser.MaxListItems"/>). It is null for <see cref="FilterOp.IsNull"/> and <see cref="FilterOp.NotNull"/>.
    /// A <see cref="DateTimeOffset"/> operand stays a <see cref="DateTimeOffset"/>, because providers read it back as one,
    /// so a hand-built <c>created_at</c> operand must be a <see cref="DateTimeOffset"/> value
    /// (<c>JsonValue.Create(instant)</c>), not a string or parsed JSON text. The property returns a detached copy on each read.
    /// </param>
    public sealed record Field(string Column, FilterOp Op, JsonNode? Value) : FilterNode
    {
        private readonly string _column = RequireText(Column, nameof(Column));

        private readonly JsonNode? _value = StrictJsonData.ToCanonicalOperand(Value);

        /// <summary>The canonical column name; a well-formed string.</summary>
        public string Column
        {
            get => _column;
            init => _column = RequireText(value, nameof(Column));
        }

        /// <summary>
        /// The operand: a canonical copy of the node it was given. Each read returns a new detached copy, so changing
        /// it or attaching it elsewhere does not change this node.
        /// </summary>
        public JsonNode? Value
        {
            get => _value?.DeepClone();
            init => _value = StrictJsonData.ToCanonicalOperand(value);
        }

        // The members below read the stored operand directly: it is canonical, so nothing in it can run caller code.
        public bool Equals(Field? other) =>
            other is not null && Column == other.Column && Op == other.Op && JsonNode.DeepEquals(_value, other._value);

        protected override bool PrintMembers(StringBuilder builder)
        {
            builder.Append("Column = ").Append(Column).Append(", Op = ").Append(Op).Append(", Value = ").Append(_value);
            return true;
        }

        // Deliberately ignores Value: numerically equal JSON numbers can serialize differently.
        public override int GetHashCode() => HashCode.Combine(Column, Op);
    }

    /// <summary>
    /// A comparison on the value at a path inside the row's metadata object (<c>Path = ["profile", "role"]</c> is
    /// <c>metadata.profile.role</c>; keys are literal, so a key may contain dots).
    /// </summary>
    /// <param name="Path">The non-empty chain of object keys.</param>
    /// <param name="Op">
    /// One of <see cref="FilterOp.Eq"/>, <see cref="FilterOp.Ne"/>, the four ordering operators,
    /// <see cref="FilterOp.Contains"/>, <see cref="FilterOp.IContains"/>, <see cref="FilterOp.IsNull"/>,
    /// <see cref="FilterOp.NotNull"/> or <see cref="FilterOp.JsonContains"/>.
    /// </param>
    /// <param name="Value">
    /// The operand as JSON. <see cref="FilterOp.Eq"/> and <see cref="FilterOp.Ne"/> take a string, number or
    /// boolean; the ordering operators a number or a string; <see cref="FilterOp.Contains"/> and
    /// <see cref="FilterOp.IContains"/> a string; <see cref="FilterOp.JsonContains"/> a non-empty array of at most
    /// <see cref="FilterParser.MaxListItems"/> strings, numbers or booleans. Null for <see cref="FilterOp.IsNull"/>
    /// and <see cref="FilterOp.NotNull"/>. Values compare only within one JSON kind (string, number, boolean); see
    /// <see cref="FilterOp"/> for the exact rules, which every provider must follow.
    /// </param>
    public sealed record MetadataPath(IReadOnlyList<string> Path, FilterOp Op, JsonNode? Value) : FilterNode
    {
        private readonly IReadOnlyList<string> _path = SnapshotPath(Path, nameof(Path));

        // The public canonical form, the one metadata is stored in: a DateTimeOffset becomes its ISO string.
        private readonly JsonNode? _value = StrictJsonData.ToCanonical(Value);

        /// <summary>The object keys: an immutable copy of the list it was given, each key a well-formed string.</summary>
        public IReadOnlyList<string> Path
        {
            get => _path;
            init => _path = SnapshotPath(value, nameof(Path));
        }

        /// <summary>
        /// The operand: a canonical copy of the node it was given, in the same form metadata is stored in (a
        /// <see cref="DateTimeOffset"/> is its ISO 8601 string). Each read returns a new detached copy, so changing it
        /// or attaching it elsewhere does not change this node.
        /// </summary>
        public JsonNode? Value
        {
            get => _value?.DeepClone();
            init => _value = StrictJsonData.ToCanonical(value);
        }

        // The members below read the stored operand directly: it is canonical, so nothing in it can run caller code.
        public bool Equals(MetadataPath? other) =>
            other is not null
            && Op == other.Op
            && Path.SequenceEqual(other.Path, StringComparer.Ordinal)
            && JsonNode.DeepEquals(_value, other._value);

        protected override bool PrintMembers(StringBuilder builder)
        {
            builder.Append("Path = [").AppendJoin(", ", Path).Append("], Op = ").Append(Op).Append(", Value = ").Append(_value?.ToJsonString());
            return true;
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Op);
            foreach (var key in Path)
            {
                hash.Add(key, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }

    private static string RequireText(string? text, string paramName)
    {
        ArgumentNullException.ThrowIfNull(text, paramName);
        StrictJsonData.RequireWellFormed(text);
        return text;
    }

    private static ReadOnlyCollection<string> SnapshotPath(IReadOnlyList<string>? path, string paramName)
    {
        ArgumentNullException.ThrowIfNull(path, paramName);
        var keys = new string[path.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = RequireText(path[i], paramName);
        }

        return Array.AsReadOnly(keys);
    }

    private static bool PrintChildren(StringBuilder builder, IReadOnlyList<FilterNode> children)
    {
        builder.Append("Children = [").AppendJoin(", ", children).Append(']');
        return true;
    }

    private static bool SameChildren(IReadOnlyList<FilterNode> left, IReadOnlyList<FilterNode> right) =>
        left.SequenceEqual(right);

    private static int HashChildren(IReadOnlyList<FilterNode> children)
    {
        var hash = new HashCode();
        foreach (var child in children)
        {
            hash.Add(child);
        }

        return hash.ToHashCode();
    }
}
