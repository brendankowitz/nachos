using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Nachos.Cli;

/// <summary>
/// Keeps what an operator typed on the command line out of anything the CLI prints. Matching is whole-token and case-insensitive:
/// SqlClient lowercases the keywords it quotes, and a token inside a longer word is not an echo.
/// </summary>
internal static class Redaction
{
    public const string Placeholder = "<redacted>";

    /// <summary>True when <paramref name="message"/> quotes <paramref name="token"/>, or contains it as a whole word.</summary>
    public static bool Echoes(string message, string token) =>
        message.Contains($"'{token}'", StringComparison.OrdinalIgnoreCase) || WholeToken(token).IsMatch(message);

    /// <summary>Replaces every whole-token occurrence of any of <paramref name="tokens"/> with <see cref="Placeholder"/>. Longest tokens first.</summary>
    public static string Scrub(string message, IEnumerable<string> tokens) =>
        tokens.OrderByDescending(token => token.Length).Aggregate(message, (text, token) => WholeToken(token).Replace(text, Placeholder));

    /// <summary>
    /// Everything in a connection string that could be sensitive: the whole string, each ';'-separated segment, and each segment's
    /// keyword and value. A segment that is not a recognised keyword is kept as a token too, because a password that contains an
    /// unescaped ';' falls apart into fragments that read as keywords. Keywords SqlClient knows ("Initial Catalog") are not secret,
    /// and keeping them readable keeps messages that name them useful.
    /// </summary>
    public static HashSet<string> ConnectionStringTokens(string connectionString)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = new SqlConnectionStringBuilder();
        Add(connectionString);
        foreach (var segment in connectionString.Split(';'))
        {
            var text = segment.Trim();
            Add(text);
            var equals = text.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var keyword = text[..equals].Trim();
            if (!known.ContainsKey(keyword))
            {
                Add(keyword);
            }

            var value = text[(equals + 1)..].Trim();
            Add(value);
            Add(value.Trim('"', '\''));
        }

        return tokens;

        void Add(string token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }
    }

    private static Regex WholeToken(string token) =>
        new($@"(?<![\w-]){Regex.Escape(token)}(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
