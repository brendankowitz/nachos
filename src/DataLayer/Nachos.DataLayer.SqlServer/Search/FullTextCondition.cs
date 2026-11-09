using System.Globalization;

namespace Nachos.DataLayer.SqlServer.Search;

/// <summary>
/// Turns the sanitized search terms of <see cref="Abstractions.Stores.IMessageSearch.LexicalAsync"/> into a full-text
/// condition for <c>CONTAINSTABLE</c>, always sent as a parameter and never written into SQL text.
/// </summary>
internal static class FullTextCondition
{
    /// <summary>The longest condition sent: <c>CONTAINSTABLE</c> refuses an <c>nvarchar(max)</c> condition.</summary>
    public const int MaxLength = 4000;

    /// <summary>
    /// The condition <c>"t1*" AND "t2*" …</c>: every term as a word prefix, quoted with its <c>"</c> doubled. Null when a
    /// term is not a plain word, or the condition would be longer than <see cref="MaxLength"/>; the search then uses
    /// <c>LIKE</c>.
    /// </summary>
    /// <remarks>
    /// A quoted term is never parsed as an operator (<c>NEAR</c>, <c>AND</c>, <c>(</c>, <c>~</c> are just words there), but
    /// the word breaker still interprets it: a <c>*</c> inside the term would make another prefix search, punctuation splits
    /// a term into a phrase (<c>lemon_lime</c> matches "lemon lime"), and a term of punctuation only matches nothing. A plain
    /// word (letters, digits and combining marks only) has none of those meanings, so only such terms go to full-text search.
    /// </remarks>
    public static string? For(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0 || !terms.All(IsPlainWord))
        {
            return null;
        }

        var condition = string.Join(" AND ", terms.Select(term => string.Concat("\"", term.Replace("\"", "\"\"", StringComparison.Ordinal), "*\"")));
        return condition.Length <= MaxLength ? condition : null;
    }

    private static bool IsPlainWord(string term) => term.Length > 0 && term.All(IsWordCharacter);

    private static bool IsWordCharacter(char c) =>
        char.IsLetterOrDigit(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}
