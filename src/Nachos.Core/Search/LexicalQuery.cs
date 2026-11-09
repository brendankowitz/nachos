using System.Globalization;
using System.Text;
using Nachos.Abstractions.Stores;

namespace Nachos.Core.Search;

/// <summary>
/// Turns a user's search text into the plain words <see cref="IMessageSearch.LexicalAsync"/> accepts. Only letters,
/// digits and combining marks survive, so no full-text operator, quote, wildcard or parenthesis can reach the store.
/// </summary>
public static class LexicalQuery
{
    /// <summary>The most terms one query contributes; the rest are ignored.</summary>
    public const int MaxTerms = 16;

    /// <summary>
    /// The query's distinct words, lower-cased (<see cref="string.ToLowerInvariant()"/>), in order of first occurrence, at
    /// most <see cref="MaxTerms"/>. A word is a maximal run of Unicode letters, digits and marks; anything else, including
    /// an unpaired surrogate, separates words. A word longer than <see cref="IMessageSearch.MaxTermLength"/> is dropped,
    /// because the store rejects it. Empty when the query has no word.
    /// </summary>
    public static IReadOnlyList<string> Terms(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;
        var position = 0;
        while (position < query.Length && terms.Count < MaxTerms)
        {
            Rune.DecodeFromUtf16(query.AsSpan(position), out var rune, out var length);
            if (!IsWordRune(rune))
            {
                Add(start, position);
                start = position + length;
            }

            position += length;
        }

        if (terms.Count < MaxTerms)
        {
            Add(start, position);
        }

        return terms;

        void Add(int from, int to)
        {
            var length = to - from;
            if (length is > 0 and <= IMessageSearch.MaxTermLength)
            {
                var term = query.Substring(from, length).ToLowerInvariant();
                if (seen.Add(term))
                {
                    terms.Add(term);
                }
            }
        }
    }

    private static bool IsWordRune(Rune rune) =>
        Rune.IsLetterOrDigit(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
}
