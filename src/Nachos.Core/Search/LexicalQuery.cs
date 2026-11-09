using System.Collections.Frozen;
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
    /// Very common English function words. Lexical search matches any term, so in a question such as "what did the user
    /// say about lemons" these would match almost every message and drown the words that carry the meaning.
    /// </summary>
    private static readonly FrozenSet<string> Stopwords = new[]
    {
        "a", "about", "an", "and", "are", "as", "at", "be", "been", "but", "by", "did", "do", "does", "for", "from", "had",
        "has", "have", "he", "her", "his", "how", "i", "if", "in", "is", "it", "its", "me", "my", "no", "not", "of", "on",
        "or", "our", "she", "so", "that", "the", "their", "them", "they", "this", "to", "was", "we", "were", "what", "when",
        "where", "which", "who", "why", "with", "you", "your",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The query's distinct words, lower-cased (<see cref="string.ToLowerInvariant()"/>), in order of first occurrence, at
    /// most <see cref="MaxTerms"/>. A word is a maximal run of Unicode letters, digits and marks; anything else, including
    /// an unpaired surrogate, separates words. A word longer than <see cref="IMessageSearch.MaxTermLength"/> is dropped,
    /// because the store rejects it. Stopwords (very common English function words) are dropped too, unless the query has
    /// no other word, so a search for "the" still searches for "the"; the cap applies after they are dropped. Empty when
    /// the query has no word.
    /// </summary>
    public static IReadOnlyList<string> Terms(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var words = new List<string>();
        var stopwords = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;
        var position = 0;
        while (position < query.Length && words.Count < MaxTerms)
        {
            Rune.DecodeFromUtf16(query.AsSpan(position), out var rune, out var length);
            if (!IsWordRune(rune))
            {
                Add(start, position);
                start = position + length;
            }

            position += length;
        }

        if (words.Count < MaxTerms)
        {
            Add(start, position);
        }

        return words.Count > 0 ? words : stopwords;

        void Add(int from, int to)
        {
            var length = to - from;
            if (length is > 0 and <= IMessageSearch.MaxTermLength)
            {
                var term = query.Substring(from, length).ToLowerInvariant();
                var list = Stopwords.Contains(term) ? stopwords : words;
                if (list.Count < MaxTerms && seen.Add(term))
                {
                    list.Add(term);
                }
            }
        }
    }

    private static bool IsWordRune(Rune rune) =>
        Rune.IsLetterOrDigit(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
}
