using Nachos.Abstractions.Domain;
using Nachos.Core.Tokens;

namespace Nachos.Core.Context;

/// <summary>A session context as assembled within its budget.</summary>
/// <param name="Messages">The newest messages after the summary's coverage that fit, oldest first.</param>
/// <param name="Summary">The summary that fit, or null.</param>
/// <param name="Representation">The representation, possibly truncated; null when none was requested.</param>
/// <param name="PeerCard">The card's entries, or null when none was requested, there is none, it is hidden or it did not fit.</param>
public sealed record AssembledContext(
    IReadOnlyList<MessageRecord> Messages,
    SummaryRecord? Summary,
    string? Representation,
    IReadOnlyList<string>? PeerCard)
{
    /// <summary>
    /// What counts against the budget (M3 D5): the content tokens of the representation, of each card entry, of the
    /// summary and of each message. The wire envelope and field names do not count.
    /// </summary>
    public int ContentTokens(ITokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        return (Representation is null ? 0 : counter.Count(Representation))
            + (PeerCard?.Sum(counter.Count) ?? 0)
            + (Summary is null ? 0 : counter.Count(Summary.Content))
            + Messages.Sum(m => counter.Count(m.Content));
    }
}
