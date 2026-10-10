using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>
/// Finds <c>az</c>, <c>azd</c>, <c>docker</c>, <c>docker-compose</c>, <c>podman</c>, <c>nerdctl</c>, <c>buildah</c> and
/// <c>bicep</c> invocations in one logical statement, in any spelling (<c>.exe</c>/<c>.cmd</c>, quoted, or by path
/// with either slash), and reports those that can reach Azure or a registry. The verb is the first token after the
/// tool's known global flags (flags are case-sensitive: docker's <c>-H</c> takes a value, <c>-h</c> does not). Only
/// the <c>bicep</c> that is az's own verb belongs to the az rule; a <c>bicep</c> anywhere else, including inside az's
/// arguments (<c>az bicep build --file "$(bicep publish ...)"</c>), is judged as standalone bicep.
/// <para>
/// Fail closed: a flag the reader does not know might take a value, and a value flag at the end has lost its
/// value, so either makes the verb undeterminable and the invocation is reported. No verb at all is fine only for
/// a bare mention (no arguments, as in prose) or <c>--version</c>/<c>--help</c>. A tool other than bicep started
/// by a launcher (<c>xargs</c>, <c>parallel</c>, <c>nohup</c>, <c>setsid</c>, <c>stdbuf</c>, <c>watch</c>) is
/// reported whatever follows, since the launcher may supply the verb; so is one that is the last word of a
/// command substitution (<c>"$(command -v azd)" up</c>, <c>`which azd` up</c>): the binary is held in a variable
/// and its verb is elsewhere. Only the bare name wrapped in its own delimiters (<c>`azd`</c> in prose,
/// <c>$(azd)</c>) is a mention.
/// </para>
/// </summary>
internal static class CliInvocations
{
    public const string AzdRule = "azd command other than package/version/config";
    public const string AzRule = "az command other than offline az bicep";
    public const string DockerRule = "docker command that reaches a registry (push, login, logout, trust, image/manifest/plugin/compose push, buildx imagetools create)";
    public const string ContainerToolRule = "docker-compose/podman/nerdctl/buildah command that reaches a registry (push, image/manifest push)";
    public const string BicepRule = "bicep command other than offline build/build-params/lint/format/decompile/decompile-params/generate-params/version";
    public const string LauncherRule = "az/azd/docker/docker-compose/podman/nerdctl/buildah started by a launcher (xargs, parallel, nohup, setsid, stdbuf, watch)";
    public const string SubstitutionRule = "az/azd/docker/docker-compose/podman/nerdctl/buildah as the last word of a command substitution (the verb is supplied elsewhere)";

    private const string Executable = @"(?<name>azd|az|docker-compose|docker|podman|nerdctl|buildah|bicep)(?:\.(?:exe|cmd|bat))?";

    // Lookbehind: not part of a longer word, variable ($az), file name (main.bicep) or option (--az), and not the
    // PowerShell module name in Install-Module/Import-Module Az; a path prefix is part of the match. The name may be
    // followed by whitespace, the end, or the close of a command substitution (`)` or a backtick).
    private static readonly Regex Program = new(
        $@"(?<![\w.$-])(?<!-(?:Module|PSResource)\s+(?:-Name\s+)?)(?:""(?:[^""]*[\\/])?{Executable}""|'(?:[^']*[\\/])?{Executable}'|(?:[^\s""'|;&()`]*[\\/])?{Executable})(?=\s|$|[)`])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Where an invocation's arguments end.
    private static readonly Regex Separator = new(@"[;|&)`]", RegexOptions.CultureInvariant);

    // One argument token (whitespace-separated, as the shell splits an unquoted command line).
    private static readonly Regex Word = new(@"\S+", RegexOptions.CultureInvariant);

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
                || (Is(verb[0], "image", "manifest", "plugin") && verb.Count > 1 && Is(verb[1], "push"))
                || (Is(verb[0], "compose") && verb.Skip(1).Any(t => Is(t, "push")))
                || (Is(verb[0], "buildx") && verb.Count > 2 && Is(verb[1], "imagetools") && Is(verb[2], "create")))),
        ["docker-compose"] = new(
            ContainerToolRule,
            Flags("--verbose", "--no-ansi", "--tls", "--tlsverify", "--skip-hostname-check", "--compatibility", "--help", "-h", "--version", "-v"),
            Flags("-f", "--file", "-p", "--project-name", "--profile", "-c", "--context", "--log-level", "--ansi", "-H", "--host",
                "--tlscacert", "--tlscert", "--tlskey", "--project-directory", "--env-file"),
            PushesNoImage),
        ["podman"] = new(
            ContainerToolRule,
            Flags("-r", "--remote", "--syslog", "--transient-store", "--help", "-h", "--version", "-v"),
            Flags("--cgroup-manager", "--conmon", "-c", "--connection", "--events-backend", "--hooks-dir", "--identity", "--log-level",
                "--module", "--namespace", "--network-cmd-path", "--network-config-dir", "--root", "--runroot", "--runtime", "--runtime-flag",
                "--ssh", "--storage-driver", "--storage-opt", "--tmpdir", "--url", "--volumepath"),
            PushesNoImage),
        ["nerdctl"] = new(
            ContainerToolRule,
            Flags("--debug", "--debug-full", "--experimental", "--insecure-registry", "--kube-hide-dupe", "--help", "-h", "--version", "-v"),
            Flags("-a", "--address", "-H", "--host", "-n", "--namespace", "--snapshotter", "--storage-driver", "--cni-path", "--cni-netconfpath",
                "--data-root", "--cgroup-manager", "--hosts-dir", "--host-gateway-ip"),
            PushesNoImage),
        ["buildah"] = new(
            ContainerToolRule,
            Flags("--debug", "--help", "-h", "--version", "-v"),
            Flags("--cgroup-manager", "--cpu-profile", "--log-level", "--memory-profile", "--registries-conf", "--registries-conf-dir", "--root",
                "--runroot", "--short-name-alias-conf", "--storage-driver", "--storage-opt", "--userns-uid-map", "--userns-gid-map"),
            PushesNoImage),
        ["bicep"] = new(
            BicepRule,
            Flags("--help", "-h", "--version", "-v", "--license", "--third-party-notices"),
            Flags(),
            // Allow-list, like `az bicep`: these never leave the machine. `test` and `snapshot` are not listed because
            // in 0.48.1 `test` restores modules unless --no-restore is given and `snapshot` takes deployment identity
            // flags (--tenant-id); everything else (restore, publish, publish-extension, deploy, local-deploy,
            // what-if, teardown, jsonrpc, console, docs, unknown) is reported.
            verb => Is(verb[0], "build", "build-params", "lint", "format", "decompile", "decompile-params", "generate-params", "version")),
    };

    /// <summary>The rules violated by <paramref name="statement"/>.</summary>
    public static IEnumerable<string> Violations(string statement)
    {
        // Where each az invocation's own verb token starts. Only that token is az's `bicep` subcommand (judged by the
        // az rule); a bicep elsewhere in az's arguments (`--file "$(bicep publish ...)"`) is a standalone bicep.
        var azVerbs = new HashSet<int>();
        foreach (Match match in Program.Matches(statement))
        {
            var name = match.Groups["name"].Value;
            if (name.Equals("bicep", StringComparison.OrdinalIgnoreCase) && azVerbs.Contains(match.Index))
            {
                continue;
            }

            var tool = Tools[name];
            var before = statement[..match.Index];
            var segment = before[(before.LastIndexOfAny([';', '|', '&']) + 1)..];
            if (!name.Equals("bicep", StringComparison.OrdinalIgnoreCase) && Launcher.IsMatch(segment))
            {
                yield return LauncherRule;
                continue;
            }

            var rest = statement[(match.Index + match.Length)..];
            if (rest.Length > 0 && rest[0] is ')' or '`')
            {
                // `"$(command -v azd)" up`, `` `which azd` up ``: the verb is outside the substitution. `` `azd` `` and
                // `$(azd)` (the name alone between its delimiters) are a mention or a bare run.
                var opener = rest[0] == ')' ? '(' : '`';
                if (!name.Equals("bicep", StringComparison.OrdinalIgnoreCase) && !(before.Length > 0 && before[^1] == opener))
                {
                    yield return SubstitutionRule;
                }

                continue;
            }

            var end = Separator.Match(rest);
            var arguments = end.Success ? rest[..end.Index] : rest;
            var words = Word.Matches(arguments);
            var tokens = words.Select(w => w.Value.Trim('"', '\'')).ToList();
            if (name.Equals("az", StringComparison.OrdinalIgnoreCase) && tool.VerbIndex(tokens) is int verb && verb < tokens.Count)
            {
                azVerbs.Add(match.Index + match.Length + words[verb].Index);
            }

            if (!tool.IsOffline(tokens))
            {
                yield return tool.Rule;
            }
        }
    }

    private static HashSet<string> Flags(params string[] flags) => new(flags, StringComparer.Ordinal);

    private static bool Is(string token, params string[] words) => words.Contains(token, StringComparer.OrdinalIgnoreCase);

    /// <summary>podman/nerdctl/buildah/docker-compose: <c>push</c>, <c>image push</c> and <c>manifest push</c> reach a registry.</summary>
    private static bool PushesNoImage(IReadOnlyList<string> verb) =>
        !(Is(verb[0], "push") || (Is(verb[0], "image", "manifest") && verb.Count > 1 && Is(verb[1], "push")));

    /// <param name="VerbIsOffline">Judges the tokens from the verb on (never empty).</param>
    private sealed record Tool(string Rule, HashSet<string> Switches, HashSet<string> ValueFlags, Func<IReadOnlyList<string>, bool> VerbIsOffline)
    {
        public bool IsOffline(List<string> tokens) => VerbIndex(tokens) switch
        {
            null => false,
            int i when i == tokens.Count => tokens.All(Informational.Contains), // no verb: a bare mention, or only --version/--help
            int i => VerbIsOffline(tokens.Skip(i).ToList()),
        };

        /// <summary>
        /// The index of the verb, the first token after the known global flags: <c>tokens.Count</c> when there is none,
        /// null when an unknown flag, or a value flag that lost its value, makes it undeterminable.
        /// </summary>
        public int? VerbIndex(List<string> tokens)
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
                    return null;
                }
            }

            return i;
        }
    }
}
