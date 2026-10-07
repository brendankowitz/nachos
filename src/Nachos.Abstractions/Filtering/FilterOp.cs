namespace Nachos.Abstractions.Filtering;

/// <summary>
/// The comparison of a <see cref="FilterNode.Field"/> or <see cref="FilterNode.MetadataPath"/>. Providers must
/// implement exactly these semantics; the shared conformance cases pin them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unset values.</b> A column or metadata path is <i>unset</i> when the column is NULL, or when the metadata key
/// is missing or holds JSON <c>null</c>. Positive operators (<see cref="Eq"/>, <see cref="In"/>, the ordering
/// operators, <see cref="Contains"/>, <see cref="IContains"/>, <see cref="JsonContains"/>, <see cref="NotNull"/>)
/// never match an unset value. <see cref="Ne"/> and <see cref="FilterNode.Not"/> do match it, because an unset value
/// is not the value being excluded. <see cref="IsNull"/> matches only unset values.
/// </para>
/// <para>
/// <b>Text</b> is compared ordinally and case-sensitively, so a provider must not rely on a case-insensitive default
/// collation; only <see cref="IContains"/> ignores case. <b>Numbers</b> compare by numeric value (<c>1</c> equals
/// <c>1.0</c>). A comparison between values of different kinds (a string against a number, say) is false, except
/// for <see cref="Ne"/>, which is then true.
/// </para>
/// </remarks>
public enum FilterOp
{
    /// <summary>The value equals the operand.</summary>
    Eq,

    /// <summary>The value does not equal the operand. Includes unset values.</summary>
    Ne,

    /// <summary>The value is greater than the operand.</summary>
    Gt,

    /// <summary>The value is greater than or equal to the operand.</summary>
    Gte,

    /// <summary>The value is less than the operand.</summary>
    Lt,

    /// <summary>The value is less than or equal to the operand.</summary>
    Lte,

    /// <summary>
    /// The value equals any element of the operand, a <see cref="System.Text.Json.Nodes.JsonArray"/> of one or more
    /// normalized values. Only produced for <see cref="FilterNode.Field"/>; on metadata an <c>in</c> filter becomes
    /// an <see cref="FilterNode.Or"/> of <see cref="Eq"/> nodes.
    /// </summary>
    In,

    /// <summary>The text value contains the operand as a case-sensitive substring.</summary>
    Contains,

    /// <summary>The text value contains the operand as a case-insensitive substring.</summary>
    IContains,

    /// <summary>The value is unset. There is no operand.</summary>
    IsNull,

    /// <summary>The value is set (the metadata key exists and is not JSON <c>null</c>). There is no operand.</summary>
    NotNull,

    /// <summary>
    /// Metadata only. The value at the path is a JSON array containing every element of the operand array
    /// (order and duplicates are irrelevant; elements compare by JSON value equality).
    /// </summary>
    JsonContains,
}
