using System.Text;

namespace Nachos.DataLayer.SqlServer.Search;

/// <summary>
/// Turns the sanitized search terms of <see cref="Abstractions.Stores.IMessageSearch.LexicalAsync"/> into the two forms
/// SQL Server matches: a full-text condition for <c>CONTAINSTABLE</c> and <c>LIKE</c> patterns. Both are always sent as
/// parameters; nothing here is ever written into SQL text.
/// </summary>
internal static class LexicalQuery
{
    /// <summary>The escape character of every <see cref="ToLikePattern"/> pattern (<c>LIKE … ESCAPE N'\'</c>).</summary>
    public const string LikeEscape = "\\";

    /// <summary>
    /// The full-text condition <c>"t1" AND "t2" …</c>, each term quoted with its <c>"</c> doubled; null when a term is not
    /// a plain word, so the search must use <c>LIKE</c>.
    /// </summary>
    /// <remarks>
    /// A quoted term is never parsed as an operator (<c>NEAR</c>, <c>AND</c>, <c>(</c>, <c>~</c> are just words there), but
    /// the word breaker still interprets it: a trailing <c>*</c> makes a prefix search, punctuation splits a term into a
    /// phrase (<c>lemon_lime</c> matches "lemon lime"), and a term of punctuation only matches nothing. A plain word, every
    /// character a letter or a digit, has none of those meanings, so only such terms go to full-text search.
    /// </remarks>
    public static string? ToFullTextCondition(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0 || !terms.All(IsPlainWord))
        {
            return null;
        }

        return string.Join(" AND ", terms.Select(term => string.Concat("\"", term.Replace("\"", "\"\"", StringComparison.Ordinal), "\"")));
    }

    /// <summary><c>%term%</c> with the term's <c>%</c>, <c>_</c>, <c>[</c> and <c>\</c> escaped, so it matches literally.</summary>
    public static string ToLikePattern(string term)
    {
        var pattern = new StringBuilder(term.Length + 8).Append('%');
        foreach (var c in term)
        {
            if (c is '%' or '_' or '[' or '\\')
            {
                pattern.Append('\\');
            }

            pattern.Append(c);
        }

        return pattern.Append('%').ToString();
    }

    private static bool IsPlainWord(string term) => term.Length > 0 && term.All(char.IsLetterOrDigit);
}
