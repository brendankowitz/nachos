using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>One forbidden Azure-touching construct found by <see cref="UnattendedAzureScanner"/>.</summary>
internal sealed record ScanHit(string File, int Line, string Pattern);

/// <summary>
/// Spec §18.3: nothing that touches Azure may run unattended. Scans <c>.github/workflows/**</c>,
/// <c>.github/actions/**</c> and <c>eng/**</c> under a root for provisioning/deployment commands, any
/// <c>az</c> command other than offline <c>az bicep</c>, Azure/registry-pushing actions and builds, and
/// reusable workflows from other repositories.
/// <para>
/// The only exemption is, inside a workflow whose triggers are exactly <c>workflow_dispatch</c>, a line within a
/// job that itself declares <c>environment: azure-live</c> (whose required reviewer is the owner). The exemption
/// is granted only when the workflow can be read unambiguously: unreadable triggers, a duplicate <c>on:</c> key,
/// an unparseable <c>jobs:</c> section, any complex (<c>? </c>) key, or any line with an unterminated quote
/// (a possible multi-line scalar hiding structure) means NO line of that workflow is exempt. This is a
/// line-oriented reader, not a YAML parser, so it errs on the side of reporting.
/// </para>
/// <para>
/// Known limit: a script run from a path the scanner does not read (for example <c>run: ./scripts/deploy.sh</c>)
/// cannot be detected statically; review such scripts by hand.
/// </para>
/// </summary>
internal sealed class UnattendedAzureScanner(string root)
{
    private static readonly Regex[] Forbidden =
    [
        Pattern(@"\bazd\s+(up|provision|deploy|down)\b"),
        // Any az command (login, group, deployment, acr, containerapp, sql, keyvault, webapp, ...) except the
        // offline `az bicep` checks that spec 18.3 allows.
        Pattern(@"\baz\s+(?!bicep\b)[a-z]"),
        Pattern(@"sqlpackage.*Publish"),
        Pattern(@"infra/hooks"),
        Pattern(@"\bdocker\s+(image\s+)?push\b"),
        // Builds that push as part of the build.
        Pattern(@"\bdocker\b.*\s--push\b"),
        Pattern(@"\bdocker\b.*\btype=registry\b"),
        // Reusable workflows from another repository (a local ./.github/workflows/x.yml stays allowed).
        Pattern(@"\buses\s*:\s*['""]?(?!\./)[^'""\s@]+/[^'""\s@]+/\.github/workflows/"),
        // Azure's own actions (login, arm-deploy, sql-action, container-apps-deploy-action, ...) and the
        // action that pushes images; either can reach Azure or a registry without a shell command.
        Pattern(@"\buses\s*:\s*['""]?azure/"),
        Pattern(@"\buses\s*:\s*['""]?docker/build-push-action"),
    ];

    private static readonly Regex KeyLine = new(
        @"^\s*(?:-\s*)?(?:""(?<key>[^""]+)""|'(?<key>[^']+)'|(?<key>[\w.-]+))\s*:(?<rest>.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ListEntry = new(
        @"^\s*-\s*(?:""(?<key>[^""]+)""|'(?<key>[^']+)'|(?<key>[\w.-]+))\s*$",
        RegexOptions.CultureInvariant);

    public IReadOnlyList<ScanHit> Scan()
    {
        var hits = new List<ScanHit>();
        foreach (var file in EnumerateFiles())
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllText(file).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            var exempt = IsWorkflow(relative) ? ExemptLines(lines) : [];

            for (var i = 0; i < lines.Length; i++)
            {
                if (exempt.Contains(i))
                {
                    continue;
                }

                foreach (var pattern in Forbidden)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        hits.Add(new ScanHit(relative, i + 1, pattern.ToString()));
                    }
                }
            }
        }

        return hits;
    }

    private static Regex Pattern(string expression) =>
        new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsWorkflow(string relative) =>
        relative.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
        && (relative.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    private IEnumerable<string> EnumerateFiles()
    {
        foreach (var relative in new[] { ".github/workflows", ".github/actions", "eng" })
        {
            var directory = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Line indexes exempt from scanning: every line of each job that declares <c>environment: azure-live</c>,
    /// but only when the workflow is dispatch-only. Empty when either fact cannot be established.
    /// </summary>
    private static HashSet<int> ExemptLines(string[] lines)
    {
        var exempt = new HashSet<int>();
        if (lines.Any(IsUnsafeToInterpret))
        {
            return exempt;
        }

        var triggers = ReadTriggers(lines);
        if (triggers is null || triggers.Count != 1 || !triggers.Contains("workflow_dispatch"))
        {
            return exempt;
        }

        foreach (var (start, end) in ReadJobs(lines))
        {
            if (DeclaresAzureLive(lines, start, end))
            {
                for (var i = start; i < end; i++)
                {
                    exempt.Add(i);
                }
            }
        }

        return exempt;
    }

    /// <summary>
    /// Reads the top-level <c>on:</c> triggers (scalar, flow list, flow mapping, block mapping or block list,
    /// quoted or not). Returns null when the section is missing or any entry is not understood.
    /// </summary>
    private static HashSet<string>? ReadTriggers(string[] lines)
    {
        var onKey = new Regex(@"^(on|""on""|'on')\s*:", RegexOptions.CultureInvariant);
        var onLine = Array.FindIndex(lines, onKey.IsMatch);
        if (onLine < 0 || lines.Count(onKey.IsMatch) > 1)
        {
            return null; // missing, or duplicated (parsers disagree on which one wins)
        }

        var triggers = new HashSet<string>(StringComparer.Ordinal);
        var inline = StripComment(lines[onLine][(lines[onLine].IndexOf(':', StringComparison.Ordinal) + 1)..]).Trim();
        if (inline.Length > 0)
        {
            return ReadInlineTriggers(inline, triggers) ? triggers : null;
        }

        int? indent = null;
        for (var i = onLine + 1; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var lineIndent = IndentOf(line);
            if (lineIndent == 0)
            {
                break;
            }

            indent ??= lineIndent;
            if (lineIndent != indent)
            {
                continue; // nested configuration of a trigger (branches:, inputs:, ...)
            }

            var entry = ListEntry.Match(line);
            if (!entry.Success)
            {
                entry = KeyLine.Match(line);
            }

            if (!entry.Success)
            {
                return null; // e.g. a complex "? key"; refuse to guess
            }

            triggers.Add(entry.Groups["key"].Value);
        }

        return triggers;
    }

    private static bool ReadInlineTriggers(string inline, HashSet<string> triggers)
    {
        var isMapping = inline.StartsWith('{') && inline.EndsWith('}');
        var isList = inline.StartsWith('[') && inline.EndsWith(']');
        if (inline.StartsWith('{') != inline.EndsWith('}') || inline.StartsWith('[') != inline.EndsWith(']'))
        {
            return false;
        }

        var items = isMapping || isList ? SplitTopLevel(inline[1..^1]) : [inline];
        foreach (var item in items)
        {
            var name = isMapping ? item.Split(':', 2)[0] : item;
            name = name.Trim().Trim('"', '\'');
            if (name.Length == 0 || !Regex.IsMatch(name, @"^[\w.-]+$"))
            {
                return false;
            }

            triggers.Add(name);
        }

        return true;
    }

    /// <summary>Splits on commas that are not nested in brackets, braces or quotes.</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                quote = c == quote ? null : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts.Where(p => p.Trim().Length > 0).ToList();
    }

    /// <summary>Line ranges [start, end) of each job under the top-level <c>jobs:</c> key; empty if unparseable.</summary>
    private static List<(int Start, int End)> ReadJobs(string[] lines)
    {
        var jobs = new List<(int Start, int End)>();
        var jobsLine = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^jobs\s*:\s*(#.*)?$"));
        if (jobsLine < 0)
        {
            return jobs;
        }

        int? jobIndent = null;
        int? current = null;
        var end = lines.Length;
        for (var i = jobsLine + 1; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var lineIndent = IndentOf(line);
            if (lineIndent == 0)
            {
                end = i;
                break;
            }

            jobIndent ??= lineIndent;
            if (lineIndent == jobIndent)
            {
                if (!KeyLine.IsMatch(line))
                {
                    return []; // fail closed
                }

                if (current is int previous)
                {
                    jobs.Add((previous, i));
                }

                current = i;
            }
        }

        if (current is int last)
        {
            jobs.Add((last, end));
        }

        return jobs;
    }

    /// <summary>
    /// True when the job's own (direct child) <c>environment</c> key is azure-live, either inline or as
    /// <c>name:</c> of a mapping. A step input that happens to be called environment does not count.
    /// </summary>
    private static bool DeclaresAzureLive(string[] lines, int start, int end)
    {
        int? childIndent = null;
        for (var i = start + 1; i < end; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            childIndent ??= IndentOf(line);
            if (IndentOf(line) != childIndent)
            {
                continue;
            }

            var match = Regex.Match(line, @"^\s*environment\s*:\s*(.*)$");
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups[1].Value.Trim().Trim('"', '\'');
            if (value == "azure-live")
            {
                return true;
            }

            if (value.Length == 0)
            {
                for (var j = i + 1; j < end; j++)
                {
                    var next = StripComment(lines[j]);
                    if (next.Trim().Length == 0)
                    {
                        continue;
                    }

                    if (IndentOf(next) <= childIndent)
                    {
                        break;
                    }

                    var name = Regex.Match(next, @"^\s*name\s*:\s*['""]?azure-live['""]?\s*$");
                    if (name.Success)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A line that could be part of a multi-line quoted scalar (unterminated quote) or a YAML complex key; either
    /// can hide structure from this line-oriented reader.
    /// </summary>
    private static bool IsUnsafeToInterpret(string line)
    {
        var code = StripComment(line, out var unterminated).Trim();
        return unterminated || code == "?" || code.StartsWith("? ", StringComparison.Ordinal);
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static string StripComment(string line) => StripComment(line, out _);

    /// <summary>Removes a trailing <c>#</c> comment (outside quotes) and reports a quote left open at line end.</summary>
    private static string StripComment(string line, out bool unterminatedQuote)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is null)
            {
                if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                {
                    unterminatedQuote = false;
                    return line[..i];
                }
            }
            else if (quote == '"' && c == '\\')
            {
                i++; // escaped character inside a double-quoted scalar
            }
            else if (c == quote)
            {
                quote = null;
            }
        }

        unterminatedQuote = quote is not null;
        return line;
    }
}
