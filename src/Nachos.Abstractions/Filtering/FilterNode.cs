using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Filtering;

/// <summary>
/// Root of the list-filter syntax tree built by <see cref="FilterParser"/>. Store list methods accept an optional
/// node and the provider compiles it to its native query; a null filter means "no filter".
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy is closed: the subtypes below are the only ones. All are sealed with <b>structural</b> equality,
/// so two trees built from the same filter compare equal. Operand <see cref="JsonNode"/>s are parser-owned and must
/// not be mutated.
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

        public override int GetHashCode() => HashChildren(Children);
    }

    /// <summary>At least one child matches. An empty list matches nothing.</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "The node names are the documented AST vocabulary; consumers are C#.")]
    public sealed record Or(IReadOnlyList<FilterNode> Children) : FilterNode
    {
        public bool Equals(Or? other) => other is not null && SameChildren(Children, other.Children);

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
    /// <see cref="long"/> for <see cref="FilterColumns.TokenCount"/>, a <see cref="bool"/> for
    /// <see cref="FilterColumns.IsActive"/>, and a <see cref="DateTimeOffset"/> at UTC for
    /// <see cref="FilterColumns.CreatedAt"/> (read it with <c>Value.GetValue&lt;DateTimeOffset&gt;()</c>; a date-only
    /// input is UTC midnight). For <see cref="FilterOp.In"/> it is a non-empty <see cref="JsonArray"/> of such
    /// values. It is null for <see cref="FilterOp.IsNull"/> and <see cref="FilterOp.NotNull"/>.
    /// </param>
    public sealed record Field(string Column, FilterOp Op, JsonNode? Value) : FilterNode
    {
        public bool Equals(Field? other) =>
            other is not null && Column == other.Column && Op == other.Op && JsonNode.DeepEquals(Value, other.Value);

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
    /// boolean; the ordering operators a number or a string (numbers compare with numbers and strings with
    /// strings; anything else does not match); <see cref="FilterOp.Contains"/> and <see cref="FilterOp.IContains"/>
    /// a string, and they match only when the value at the path is itself a string that contains it (a
    /// case-sensitive or case-insensitive substring match); <see cref="FilterOp.JsonContains"/> a non-empty array.
    /// Null for <see cref="FilterOp.IsNull"/> and <see cref="FilterOp.NotNull"/>.
    /// </param>
    public sealed record MetadataPath(IReadOnlyList<string> Path, FilterOp Op, JsonNode? Value) : FilterNode
    {
        public bool Equals(MetadataPath? other) =>
            other is not null
            && Op == other.Op
            && Path.SequenceEqual(other.Path, StringComparer.Ordinal)
            && JsonNode.DeepEquals(Value, other.Value);

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
