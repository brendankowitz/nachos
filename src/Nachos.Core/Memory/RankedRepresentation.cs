using System.Text;
using Nachos.Abstractions.Domain;

namespace Nachos.Core.Memory;

/// <summary>
/// The visible conclusions a working representation keeps, best-ranked first (see <see cref="RepresentationBuilder"/>).
/// The first n of them are exactly what the representation keeps when it may hold only n.
/// </summary>
public sealed class RankedRepresentation
{
    private readonly IReadOnlyList<(int Rank, ConclusionRecord Conclusion)> _ranked;

    internal RankedRepresentation(IReadOnlyList<(int Rank, ConclusionRecord Conclusion)> ranked)
    {
        _ranked = ranked;
    }

    public int Count => _ranked.Count;

    /// <summary>
    /// The Markdown of the <paramref name="count"/> best-ranked conclusions: one section per level that has any, in level
    /// order, each a bullet list ordered by rank, then creation time, then id, so the same memory always renders the same
    /// text. Zero gives the empty string.
    /// </summary>
    public string Format(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Count);
        var text = new StringBuilder();
        var sections = _ranked
            .Take(count)
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.Conclusion.CreatedAt)
            .ThenBy(c => c.Conclusion.PublicId, StringComparer.Ordinal)
            .GroupBy(c => c.Conclusion.Level)
            .OrderBy(section => section.Key);
        foreach (var section in sections)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append("## ").Append(section.Key).Append("\n\n");
            foreach (var (_, conclusion) in section)
            {
                // API-created content may span lines; one line per bullet keeps the list structure intact.
                text.Append("- ").Append(conclusion.Content.ReplaceLineEndings(" ")).Append('\n');
            }
        }

        return text.ToString();
    }
}
