using System.Text;

namespace Nachos.DataLayer.SqlServer.Search;

/// <summary>The substring <c>LIKE</c> pattern of a search term, used when full-text search is not.</summary>
internal static class LikePattern
{
    /// <summary>The escape character of every pattern (<c>LIKE … ESCAPE N'\'</c>).</summary>
    public const string Escape = "\\";

    /// <summary><c>%term%</c> with the term's <c>%</c>, <c>_</c>, <c>[</c> and <c>\</c> escaped, so it matches literally.</summary>
    public static string For(string term)
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
}
