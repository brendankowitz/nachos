using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>
/// Finds <c>az</c>/<c>azd</c> invocations in one logical statement, in any spelling (<c>az.cmd</c>, <c>azd.exe</c>,
/// quoted, or by path with either slash), and reports those that are not offline. The verb is the first token
/// after the global flags; a flag the reader does not know might take a value, so it makes the verb
/// undeterminable and the invocation is reported. Offline: <c>azd package|version|config</c>, and
/// <c>az bicep build|build-params|lint|format|decompile|version|install|upgrade</c>; no verb at all (help) too.
/// </summary>
internal static class AzureCliInvocations
{
    public const string AzdRule = "azd command other than package/version/config";
    public const string AzRule = "az command other than offline az bicep";

    private const string Executable = @"(?<name>azd|az)(?:\.(?:exe|cmd|bat))?";

    // Lookbehind: not part of a longer word, variable ($az) or option (--az); a path prefix is part of the match.
    private static readonly Regex Program = new(
        $@"(?<![\w.$-])(?:""(?:[^""]*[\\/])?{Executable}""|'(?:[^']*[\\/])?{Executable}'|(?:[^\s""'|;&()`]*[\\/])?{Executable})(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Where an invocation's arguments end.
    private static readonly Regex Separator = new(@"[;|&)`]", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AzdSwitches = new(["--debug", "--no-prompt", "--docs", "--help", "-h", "--version"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AzdValueFlags = new(["-e", "--environment", "-C", "--cwd", "-o", "--output", "--trace-log-file", "--trace-log-url"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AzdOfflineVerbs = new(["package", "version", "config"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AzSwitches = new(["--debug", "--verbose", "--only-show-errors", "--help", "-h", "--version"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AzValueFlags = new(["-o", "--output", "--query", "--subscription"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OfflineBicepCommands = new(
        ["build", "build-params", "lint", "format", "decompile", "version", "install", "upgrade"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The rule names (<see cref="AzdRule"/>, <see cref="AzRule"/>) violated by <paramref name="statement"/>.</summary>
    public static IEnumerable<string> Violations(string statement)
    {
        foreach (Match match in Program.Matches(statement))
        {
            var rest = statement[(match.Index + match.Length)..];
            var end = Separator.Match(rest);
            var tokens = (end.Success ? rest[..end.Index] : rest)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim('"', '\''))
                .ToList();

            var isAzd = match.Groups["name"].Value.Equals("azd", StringComparison.OrdinalIgnoreCase);
            if (isAzd ? !IsOfflineAzd(tokens) : !IsOfflineAz(tokens))
            {
                yield return isAzd ? AzdRule : AzRule;
            }
        }
    }

    private static bool IsOfflineAzd(List<string> tokens)
    {
        var verb = VerbIndex(tokens, AzdSwitches, AzdValueFlags);
        return verb is int index && (index == tokens.Count || AzdOfflineVerbs.Contains(tokens[index]));
    }

    private static bool IsOfflineAz(List<string> tokens)
    {
        var verb = VerbIndex(tokens, AzSwitches, AzValueFlags);
        if (verb is not int index)
        {
            return false;
        }

        if (index == tokens.Count)
        {
            return true;
        }

        // `az bicep` alone prints help; anything after it must be an offline subcommand.
        return tokens[index].Equals("bicep", StringComparison.OrdinalIgnoreCase)
            && (index + 1 == tokens.Count || OfflineBicepCommands.Contains(tokens[index + 1]));
    }

    /// <summary>
    /// Index of the first non-flag token (tokens.Count when there is none), or null when an unknown flag makes it
    /// undeterminable.
    /// </summary>
    private static int? VerbIndex(List<string> tokens, HashSet<string> switches, HashSet<string> valueFlags)
    {
        var i = 0;
        while (i < tokens.Count && tokens[i].StartsWith('-'))
        {
            var flag = tokens[i];
            if (flag.Contains('=', StringComparison.Ordinal) || switches.Contains(flag))
            {
                i++;
            }
            else if (valueFlags.Contains(flag))
            {
                i += 2;
            }
            else
            {
                return null;
            }
        }

        return Math.Min(i, tokens.Count);
    }
}
