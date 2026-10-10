namespace Nachos.Abstractions.Filtering;

/// <summary>
/// The comparison of a <see cref="FilterNode.Field"/> or <see cref="FilterNode.MetadataPath"/>. Providers must
/// implement exactly these semantics; the shared conformance cases pin them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unset values.</b> A column is <i>unset</i> when it is NULL. A metadata path is unset when the key is missing
/// or holds JSON <c>null</c>; an object or array value is <b>set</b>, so <see cref="NotNull"/> (the wildcard
/// <c>"*"</c>) matches any non-null value including objects and arrays, and <see cref="IsNull"/> never matches them.
/// Positive operators (<see cref="Eq"/>, <see cref="In"/>, the ordering operators, <see cref="Contains"/>,
/// <see cref="IContains"/>, <see cref="JsonContains"/>, <see cref="NotNull"/>) never match an unset value.
/// <see cref="Ne"/> and <see cref="FilterNode.Not"/> do match it, because an unset value is not the value being
/// excluded. <see cref="IsNull"/> matches only unset values.
/// </para>
/// <para>
/// <b>Kinds.</b> JSON values compare only within the same kind: string, number or boolean. A value of another kind
/// is never equal to the operand, never ordered against it, and never "contains" it. So <see cref="Eq"/>, the four
/// ordering operators, <see cref="Contains"/>, <see cref="IContains"/> and <see cref="In"/> membership are false, and
/// <see cref="Ne"/> is true. The number <c>5</c> and the string <c>"5"</c> are different, as are <c>true</c> and
/// <c>"true"</c>. <b>Providers must check the JSON value type before comparing</b>; comparing the text that a
/// function such as SQL <c>JSON_VALUE</c> returns is not enough, because it renders every scalar kind as text.
/// </para>
/// <para>
/// <b>Text</b> is compared ordinally and case-sensitively by <see cref="Eq"/>, <see cref="Ne"/>, <see cref="In"/>,
/// the ordering operators and <see cref="Contains"/>, for column text and metadata strings alike. Whitespace is
/// significant (<c>"ok "</c> is not <c>"ok"</c>). A provider must not rely on a case-insensitive default collation.
/// <b>Provider note:</b> SQL Server's <c>=</c>, <c>&lt;&gt;</c> and <c>IN</c> ignore trailing spaces even under a
/// <c>BIN2</c> collation, so a SQL provider must add a length or <c>DATALENGTH</c> check (or an equivalent) to honor
/// the trailing-space rule.
/// </para>
/// <para>
/// <b>Case folding.</b> <see cref="IContains"/> is case-insensitive by invariant simple uppercasing per character:
/// the in-memory equivalent is comparing <c>ToUpperInvariant()</c> of both sides. Conformance cases are ASCII-only;
/// folding of non-ASCII text is provider-defined. A SQL provider should approximate the rule with <c>UPPER()</c> on
/// both sides under a binary (<c>BIN2</c>) collation, <b>not</b> a case-insensitive collation such as
/// <c>CI_AS</c>, which would also fold accents, kana and width.
/// </para>
/// <para>
/// <b>Numbers</b> compare by numeric value (<c>1</c> equals <c>1.0</c>). Metadata keys match literally: a key may
/// contain dots, quotes, brackets or apostrophes, so a provider must quote or escape path segments.
/// </para>
/// </remarks>
public enum FilterOp
{
    /// <summary>The value equals the operand.</summary>
    Eq,

    /// <summary>The value does not equal the operand, including values of another kind and unset values.</summary>
    Ne,

    /// <summary>The value is greater than the operand (same kind only).</summary>
    Gt,

    /// <summary>The value is greater than or equal to the operand (same kind only).</summary>
    Gte,

    /// <summary>The value is less than the operand (same kind only).</summary>
    Lt,

    /// <summary>The value is less than or equal to the operand (same kind only).</summary>
    Lte,

    /// <summary>
    /// The value equals any element of the operand, a <see cref="System.Text.Json.Nodes.JsonArray"/> of one or more
    /// normalized values. Only produced for <see cref="FilterNode.Field"/>; on metadata an <c>in</c> filter becomes
    /// an <see cref="FilterNode.Or"/> of <see cref="Eq"/> nodes.
    /// </summary>
    In,

    /// <summary>
    /// Text column: the value contains the operand as a case-sensitive substring. Session <c>PeerId</c>: an active
    /// member's peer name does. Metadata: the value at the path is a string containing the operand as a substring,
    /// <b>or</b> an array with a string element equal to the operand (array elements must be equal, not substrings;
    /// non-string elements never match).
    /// </summary>
    Contains,

    /// <summary>
    /// As <see cref="Contains"/>, but case-insensitive (see "Case folding" above); for a metadata array the string
    /// element must equal the operand under the same folding.
    /// </summary>
    IContains,

    /// <summary>The value is unset. There is no operand.</summary>
    IsNull,

    /// <summary>The value is set (the metadata key exists and is not JSON <c>null</c>). There is no operand.</summary>
    NotNull,

    /// <summary>
    /// Metadata only. The value at the path is a JSON array containing every element of the operand array (order and
    /// duplicates are irrelevant). Operand elements are strings, numbers or booleans, compared by kind and value
    /// (numbers numerically, so <c>7.0</c> matches <c>7</c>; <c>1</c> does not match <c>"1"</c>).
    /// </summary>
    JsonContains,
}
