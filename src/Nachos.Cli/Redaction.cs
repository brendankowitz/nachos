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

    // Where and as whom the CLI connects, under every keyword SqlClient accepts for it. These values are redacted at any length,
    // even when they are ordinary words: a password of "login" must not survive in "Login failed".
    private static readonly HashSet<string> SensitiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password", "PWD",
        "User ID", "UID", "User",
        "Data Source", "Server", "Address", "Addr", "Network Address", "Failover Partner",
        "Initial Catalog", "Database",
    };

    /// <summary>
    /// What in a connection string could be sensitive: the whole string; each ';'-separated segment; the value of every sensitive
    /// keyword (password, user, server, database), both as written and as SqlClient parsed it, since quoting and doubled quotes can
    /// make the two differ; and every keyword and value that SqlClient does not recognise, because a value with an unescaped ';' falls
    /// apart into fragments that read as keywords. Recognised keywords, and the values of the other recognised keywords (booleans,
    /// numbers, timeouts, Application Name), stay readable so that messages naming them stay useful. A token with no letter or digit
    /// is never redacted: "Server=." must not blank out every period.
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
                AddValue(text);
                continue;
            }

            var keyword = text[..equals].Trim();
            var recognised = known.ContainsKey(keyword);
            if (!recognised)
            {
                Add(keyword);
            }

            if (!recognised || SensitiveKeywords.Contains(keyword))
            {
                AddValue(text[(equals + 1)..]);
            }
        }

        if (Parse(connectionString) is { } parsed)
        {
            foreach (var value in new[] { parsed.Password, parsed.UserID, parsed.DataSource, parsed.FailoverPartner, parsed.InitialCatalog })
            {
                AddValue(value);
                foreach (var part in value.Split(';'))
                {
                    AddValue(part);
                }
            }
        }

        return tokens;

        void AddValue(string value)
        {
            var trimmed = value.Trim();
            Add(trimmed);
            Add(trimmed.Trim('"', '\''));
        }

        void Add(string token)
        {
            if (token.Any(char.IsLetterOrDigit))
            {
                tokens.Add(token);
            }
        }
    }

    // This runs while a failure is being reported, so it must not throw itself; a string SqlClient cannot parse still has its raw
    // segments redacted.
    private static SqlConnectionStringBuilder? Parse(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A server may escape the quotes in a name it echoes (SQL Server reports the user Ab'Cd as 'Ab\'Cd'), so a quote in the token
    // also matches with a backslash before it.
    private static Regex WholeToken(string token) =>
        new($@"(?<![\w-]){string.Concat(token.Select(EscapedChar))}(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string EscapedChar(char c) => c is '\'' or '"' ? $@"\\?{c}" : Regex.Escape(c.ToString());
}
