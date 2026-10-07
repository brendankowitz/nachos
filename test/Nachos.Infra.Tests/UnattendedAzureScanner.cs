using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>One forbidden Azure-touching construct found by <see cref="UnattendedAzureScanner"/>.</summary>
internal sealed record ScanHit(string File, int Line, string Pattern);

/// <summary>
/// Spec §18.3: nothing that touches Azure may run unattended. Scans <c>.github/workflows/**</c> and
/// <c>eng/**</c> under a root for provisioning/deployment commands. The only exemption is a workflow whose
/// triggers are exactly <c>workflow_dispatch</c> AND which runs in the <c>azure-live</c> environment
/// (whose required reviewer is the owner).
/// </summary>
internal sealed class UnattendedAzureScanner(string root)
{
    private static readonly Regex[] Forbidden =
    [
        Pattern(@"\bazd\s+(up|provision|deploy|down)\b"),
        Pattern(@"\baz\s+deployment\b"),
        Pattern(@"\baz\s+group\b"),
        Pattern(@"sqlpackage.*Publish"),
        Pattern(@"infra/hooks"),
        Pattern(@"\bdocker\s+push\b"),
        Pattern(@"\baz\s+acr\b"),
    ];

    private static readonly Regex AzureLiveEnvironment = new(
        @"^[ \t]*environment[ \t]*:[ \t]*(\r?\n[ \t]+name[ \t]*:[ \t]*)?['""]?azure-live['""]?[ \t]*(#.*)?\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public IReadOnlyList<ScanHit> Scan()
    {
        var hits = new List<ScanHit>();
        foreach (var file in EnumerateFiles())
        {
            var text = File.ReadAllText(file);
            if (IsExemptWorkflow(file, text))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
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

    private IEnumerable<string> EnumerateFiles()
    {
        foreach (var relative in new[] { ".github/workflows", "eng" })
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

    private bool IsExemptWorkflow(string file, string text)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (!relative.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
            || !(relative.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                 || relative.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var triggers = ReadTriggers(text);
        return triggers.Count == 1
            && triggers.Contains("workflow_dispatch")
            && AzureLiveEnvironment.IsMatch(text);
    }

    /// <summary>Reads the top-level <c>on:</c> triggers in scalar, flow-list, and block forms.</summary>
    private static HashSet<string> ReadTriggers(string text)
    {
        var triggers = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var onLine = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^(on|""on""|'on')\s*:"));
        if (onLine < 0)
        {
            return triggers;
        }

        var inline = StripComment(lines[onLine][(lines[onLine].IndexOf(':', StringComparison.Ordinal) + 1)..]).Trim();
        if (inline.Length > 0)
        {
            foreach (var name in inline.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                triggers.Add(name.Trim('"', '\''));
            }

            return triggers;
        }

        int? indent = null;
        for (var i = onLine + 1; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var lineIndent = line.Length - line.TrimStart().Length;
            if (lineIndent == 0)
            {
                break;
            }

            indent ??= lineIndent;
            if (lineIndent != indent)
            {
                continue;
            }

            var entry = Regex.Match(line.Trim(), @"^(?:-\s*)?([\w-]+)\s*:?");
            if (entry.Success)
            {
                triggers.Add(entry.Groups[1].Value);
            }
        }

        return triggers;
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf('#', StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }
}
