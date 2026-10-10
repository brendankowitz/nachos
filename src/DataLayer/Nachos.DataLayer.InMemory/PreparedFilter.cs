using Nachos.Abstractions.Filtering;

namespace Nachos.DataLayer.InMemory;

/// <summary>
/// A <see cref="FilterNode"/> compiled once per query by <see cref="InMemoryFilterEvaluator.Prepare"/>, so that
/// evaluating it per row never touches the caller's tree again.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FilterNode.Field.Value"/> and <see cref="FilterNode.MetadataPath.Value"/> return a fresh deep copy on
/// every read, so the operands are read exactly once, here, and shared by every row. The compiled form holds no
/// reference to the source tree: later changes to it (or to a children list the caller kept) have no effect.
/// </para>
/// <para>
/// Immutable, so safe to share between threads. It is independent of the record kind: a column that does not exist on
/// the kind being evaluated throws <see cref="NotSupportedException"/> when a row reaches that node, as it always did.
/// </para>
/// </remarks>
internal sealed class PreparedFilter
{
    // Test seam: sees every filter prepared by the current async flow, to prove a store call prepares exactly once.
    internal static readonly AsyncLocal<Action<PreparedFilter>?> Observer = new();

    private readonly Func<InMemoryFilterEvaluator.Row, bool> _predicate;

    internal PreparedFilter(Func<InMemoryFilterEvaluator.Row, bool> predicate, int operandReads)
    {
        _predicate = predicate;
        OperandReads = operandReads;
        Observer.Value?.Invoke(this);
    }

    /// <summary>How many operand reads (<c>Value</c> getter calls) preparing performed: one per Field or MetadataPath leaf.</summary>
    internal int OperandReads { get; }

    internal bool Matches(InMemoryFilterEvaluator.Row row) => _predicate(row);
}
