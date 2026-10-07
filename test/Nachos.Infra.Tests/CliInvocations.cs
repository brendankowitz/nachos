using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>
/// Finds <c>az</c>, <c>azd</c>, <c>docker</c> and <c>bicep</c> invocations in one logical statement, in any spelling
/// (<c>.exe</c>/<c>.cmd</c>, quoted, or by path with either slash), and reports those that can reach Azure or a
/// registry. The verb is the first token after the tool's known global flags (flags are case-sensitive: docker's
/// <c>-H</c> takes a value, <c>-h</c> does not).
/// <para>
/// Fail closed: a flag the reader does not know might take a value, and a value flag at the end has lost its
/// value, so either makes the verb undeterminable and the invocation is reported. No verb at all is fine only for
/// a bare mention (no arguments, as in prose) or <c>--version</c>/<c>--help</c>. <c>az</c>/<c>azd</c> started by
/// a launcher (<c>xargs</c>, <c>parallel</c>, <c>nohup</c>, <c>setsid</c>, <c>stdbuf</c>, <c>watch</c>) are reported
/// whatever follows, since the launcher may supply the verb.
/// </para>
/// </summary>
internal static class CliInvocations
{
    public const string AzdRule = "azd command other than package/version/config";
    public const string AzRule = "az command other than offline az bicep";
    public const string DockerRule = "docker command that reaches a registry (push, login, logout, trust, image/manifest/compose push, buildx imagetools create)";
    public const string BicepRule = "bicep command that reaches a registry or Azure (publish, restore, deploy, local-deploy, what-if)";
    public const string LauncherRule = "az/azd started by a launcher (xargs, parallel, nohup, setsid, stdbuf, watch)";

    private const string Executable = @"(?<name>azd|az|docker|bicep)(?:\.(?:exe|cmd|bat))?";

    // Lookbehind: not part of a longer word, variable ($az), file name (main.bicep) or option (--az), and not the
    // PowerShell module name in Install-Module/Import-Module Az; a path prefix is part of the match.
    private static readonly Regex Program = new(
        $@"(?<![\w.$-])(?<!-(?:Module|PSResource)\s+(?:-Name\s+)?)(?:""(?:[^""]*[\\/])?{Executable}""|'(?:[^']*[\\/])?{Executable}'|(?:[^\s""'|;&()`]*[\\/])?{Executable})(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Where an invocation's arguments end.
    private static readonly Regex Separator = new(@"[;|&)`]", RegexOptions.CultureInvariant);

    private static readonly Regex Launcher = new(@"\b(?:xargs|parallel|nohup|setsid|stdbuf|watch)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Informational = new(["--version", "-v", "--help", "-h"], StringComparer.Ordinal);

    private static readonly Dictionary<string, Tool> Tools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["azd"] = new(
            AzdRule,
            Flags("--debug", "--no-prompt", "--docs", "--help", "-h", "--version"),
            Flags("-e", "--environment", "-C", "--cwd", "-o", "--output", "--trace-log-file", "--trace-log-url"),
            verb => Is(verb[0], "package", "version", "config")),
        ["az"] = new(
            AzRule,
            Flags("--debug", "--verbose", "--only-show-errors", "--help", "-h", "--version"),
            Flags("-o", "--output", "--query", "--subscription"),
            // `az bicep` alone prints help; anything after it must be an offline subcommand.
            verb => Is(verb[0], "bicep")
                && (verb.Count == 1 || Is(verb[1], "build", "build-params", "lint", "format", "decompile", "version", "install", "upgrade"))),
        ["docker"] = new(
            DockerRule,
            Flags("-D", "--debug", "--tls", "--tlsverify", "--help", "-h", "--version", "-v"),
            Flags("--config", "-H", "--host", "--context", "-c", "-l", "--log-level", "--tlscacert", "--tlscert", "--tlskey"),
            verb => !(Is(verb[0], "push", "login", "logout", "trust")
                || (Is(verb[0], "image", "manifest") && verb.Count > 1 && Is(verb[1], "push"))
                || (Is(verb[0], "compose") && verb.Skip(1).Any(t => Is(t, "push")))
                || (Is(verb[0], "buildx") && verb.Count > 2 && Is(verb[1], "imagetools") && Is(verb[2], "create")))),
        ["bicep"] = new(
            BicepRule,
            Flags("--help", "-h", "--version", "-v", "--license", "--third-party-notices"),
            Flags(),
            verb => !Is(verb[0], "publish", "restore", "deploy", "local-deploy", "what-if")),
    };

    /// <summary>The rules violated by <paramref name="statement"/>.</summary>
    public static IEnumerable<string> Violations(string statement)
    {
        foreach (Match match in Program.Matches(statement))
        {
            var name = match.Groups["name"].Value;
            var tool = Tools[name];
            var before = statement[..match.Index];
            var segment = before[(before.LastIndexOfAny([';', '|', '&']) + 1)..];
            if (!name.Equals("docker", StringComparison.OrdinalIgnoreCase) && !name.Equals("bicep", StringComparison.OrdinalIgnoreCase)
                && Launcher.IsMatch(segment))
            {
                yield return LauncherRule;
                continue;
            }

            var rest = statement[(match.Index + match.Length)..];
            var end = Separator.Match(rest);
            var tokens = (end.Success ? rest[..end.Index] : rest)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim('"', '\''))
                .ToList();

            if (!tool.IsOffline(tokens))
            {
                yield return tool.Rule;
            }
        }
    }

    private static HashSet<string> Flags(params string[] flags) => new(flags, StringComparer.Ordinal);

    private static bool Is(string token, params string[] words) => words.Contains(token, StringComparer.OrdinalIgnoreCase);

    /// <param name="VerbIsOffline">Judges the tokens from the verb on (never empty).</param>
    private sealed record Tool(string Rule, HashSet<string> Switches, HashSet<string> ValueFlags, Func<IReadOnlyList<string>, bool> VerbIsOffline)
    {
        public bool IsOffline(List<string> tokens)
        {
            var i = 0;
            while (i < tokens.Count && tokens[i].StartsWith('-'))
            {
                var flag = tokens[i];
                if (flag.Contains('=', StringComparison.Ordinal) || Switches.Contains(flag))
                {
                    i++;
                }
                else if (ValueFlags.Contains(flag) && i + 1 < tokens.Count)
                {
                    i += 2;
                }
                else
                {
                    return false; // unknown flag, or a value flag that lost its value: verb undeterminable
                }
            }

            if (i == tokens.Count)
            {
                // No verb: a bare mention, or only --version/--help.
                return tokens.All(Informational.Contains);
            }

            return VerbIsOffline(tokens.Skip(i).ToList());
        }
    }
}
