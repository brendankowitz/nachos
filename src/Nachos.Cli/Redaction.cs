using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Nachos.Cli;

/// <summary>
/// Keeps what an operator typed on the command line out of anything the CLI prints. Matching is whole-token, so a token inside a
/// longer word is not an echo. Parser messages are matched case-sensitively, because the parser quotes what it was given verbatim;
/// connection-string tokens case-insensitively, because SqlClient lowercases the keywords it quotes.
/// </summary>
internal static class Redaction
{
    public const string Placeholder = "<redacted>";

    /// <summary>True when <paramref name="message"/> quotes <paramref name="token"/>, or contains it as a whole word, in the same case.</summary>
    public static bool Echoes(string message, string token) =>
        message.Contains($"'{token}'", StringComparison.Ordinal) || WholeToken(token, RegexOptions.None).IsMatch(message);

    /// <summary>
    /// Replaces every whole-token occurrence of any of <paramref name="tokens"/>, in any case, with <see cref="Placeholder"/>.
    /// Longest tokens first.
    /// </summary>
    public static string Scrub(string message, IEnumerable<string> tokens) =>
        tokens.OrderByDescending(token => token.Length)
            .Aggregate(message, (text, token) => WholeToken(token, RegexOptions.IgnoreCase).Replace(text, Placeholder));

    // Where a server is, under every keyword SqlClient accepts for it.
    private static readonly HashSet<string> HostKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data Source", "Server", "Address", "Addr", "Network Address", "Failover Partner",
    };

    // The database file to attach, under every keyword SqlClient accepts for it.
    private static readonly HashSet<string> FileKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AttachDbFilename", "Extended Properties", "Initial File Name",
    };

    // Where and as whom the CLI connects. These values are redacted at any length, even when they are ordinary words: a password of
    // "login" must not survive in "Login failed".
    private static readonly HashSet<string> SensitiveKeywords = new(
        ["Password", "PWD", "User ID", "UID", "User", "Initial Catalog", "Database", .. HostKeywords, .. FileKeywords],
        StringComparer.OrdinalIgnoreCase);

    // Protocol prefixes a server value may carry ("tcp:host"); the prefix is not part of the name a server reports.
    private static readonly HashSet<string> HostProtocols = new(StringComparer.OrdinalIgnoreCase) { "tcp", "np", "lpc", "admin" };

    /// <summary>
    /// What in a connection string could be sensitive: the whole string; each ';'-separated segment; the value of every sensitive
    /// keyword (password, user, server, failover partner, database, database file), both as written and as SqlClient parsed it,
    /// since quoting and doubled quotes can make the two differ; and every keyword and value that SqlClient does not recognise,
    /// because a value with an unescaped ';' falls apart into fragments that read as keywords. A server also yields the pieces a
    /// server reports on their own (see <c>AddHost</c>), and a database file its file name. Recognised keywords, and the values of the
    /// other recognised keywords (booleans, numbers, timeouts, Application Name), stay readable so that messages naming them stay
    /// useful. A token with no letter or digit is never redacted: "Server=." must not blank out every period.
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

            var value = text[(equals + 1)..];
            if (!recognised || SensitiveKeywords.Contains(keyword))
            {
                AddValue(value);
            }

            if (recognised && HostKeywords.Contains(keyword))
            {
                AddHost(value);
            }

            if (recognised && FileKeywords.Contains(keyword))
            {
                AddFile(value);
            }
        }

        if (Parse(connectionString) is { } parsed)
        {
            foreach (var value in new[]
            {
                parsed.Password, parsed.UserID, parsed.DataSource, parsed.FailoverPartner, parsed.InitialCatalog, parsed.AttachDBFilename,
            })
            {
                AddValue(value);
                foreach (var part in value.Split(';'))
                {
                    AddValue(part);
                }
            }

            AddHost(parsed.DataSource);
            AddHost(parsed.FailoverPartner);
            AddFile(parsed.AttachDBFilename);
        }

        return tokens;

        // A server reports its name without the protocol prefix, port or instance ("tcp:Name.database.windows.net,1433" can come back
        // as 'Name.database.windows.net' or as 'Name'), and an instance on its own. So the host, its first DNS label and the instance
        // are tokens too. An IP address has no DNS label: "127" is not a name.
        void AddHost(string value)
        {
            var host = value.Trim().Trim('"', '\'');
            var colon = host.IndexOf(':');
            if (colon > 0 && HostProtocols.Contains(host[..colon].Trim()))
            {
                host = host[(colon + 1)..];
            }

            var comma = host.LastIndexOf(',');
            if (comma >= 0)
            {
                host = host[..comma];
            }

            // A named pipe is written "\\host\pipe\...": the host follows the leading backslashes.
            host = host.Trim().TrimStart('\\');
            var backslash = host.IndexOf('\\');
            if (backslash >= 0)
            {
                AddValue(host[(backslash + 1)..]);
                host = host[..backslash];
            }

            host = host.Trim();
            AddValue(host);
            var dot = host.IndexOf('.');
            if (dot > 0 && !IPAddress.TryParse(host, out _))
            {
                AddValue(host[..dot]);
            }
        }

        // A server reports a database file by its full path, which SqlClient may have expanded (|DataDirectory|), so the file name
        // is a token on its own.
        void AddFile(string value)
        {
            var path = value.Trim().Trim('"', '\'');
            AddValue(path);
            AddValue(path[(path.LastIndexOfAny(['\\', '/']) + 1)..]);
        }

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
    private static Regex WholeToken(string token, RegexOptions options) =>
        new($@"(?<![\w-]){string.Concat(token.Select(EscapedChar))}(?![\w-])", RegexOptions.CultureInvariant | options);

    private static string EscapedChar(char c) => c is '\'' or '"' ? $@"\\?{c}" : Regex.Escape(c.ToString());
}
