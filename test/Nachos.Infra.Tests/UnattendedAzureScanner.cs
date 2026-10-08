using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>One forbidden Azure-touching construct found by <see cref="UnattendedAzureScanner"/>.</summary>
internal sealed record ScanHit(string File, int Line, string Pattern);

/// <summary>
/// Spec §18.3: nothing that touches Azure may run unattended. Scans <c>.github/workflows/**</c>,
/// <c>.github/actions/**</c>, <c>.github/scripts/**</c> and <c>eng/**</c> under a root for az/azd/docker/bicep (and
/// docker-compose/podman/nerdctl/buildah) commands that are not offline (<see cref="CliInvocations"/>), Az PowerShell, Azure endpoints (ARM, Entra, storage,
/// Key Vault, SQL, App Service, sovereign clouds), Azure actions, registry logins, pushes (any tool) and ACR
/// references, and reusable workflows from other repositories. Patterns are matched per logical statement (shell and
/// PowerShell line continuations and YAML folded <c>run: &gt;</c> blocks joined, <c>#</c> comments stripped when
/// quoting is unambiguous) and reported at the statement's first physical line.
/// <para>
/// <c>.github/scripts</c> also holds JavaScript, docs and test fixtures, where words like <c>az</c> are ordinary
/// identifiers or prose. There, <c>node_modules/</c> (created by <c>npm ci</c>), <c>fixtures/</c> and <c>*.md</c>
/// are skipped, and <c>*.js|mjs|cjs|ts|json</c> are checked only for unambiguous commands (<c>azd</c> with a
/// deploying verb, <c>docker push</c>, ACR and ARM endpoints, Az cmdlets). Limit: JavaScript that shells out to
/// <c>az</c> through a string is detected only when the string holds such an unambiguous command. Every other
/// file there, and every file under the other roots (including any tracked <c>node_modules</c>), gets all rules.
/// </para>
/// <para>
/// The only exemption is, inside a workflow whose triggers are exactly <c>workflow_dispatch</c>, a line within a
/// job that itself declares <c>environment: azure-live</c> exactly once (whose required reviewer is the owner).
/// The exemption is granted only when the workflow can be read unambiguously: unreadable triggers, a duplicate
/// <c>on:</c> key, an unparseable <c>jobs:</c> section, any complex (<c>? </c>) key, or any line with an
/// unterminated quote or unbalanced <c>[</c>/<c>{</c> (a possible multi-line scalar or flow collection hiding
/// structure) means NO line of that workflow is exempt. This is a line-oriented reader, not a YAML parser, so it
/// errs on the side of reporting.
/// </para>
/// <para>
/// Known limit: a script run from a path the scanner does not read (for example <c>run: ./scripts/deploy.sh</c>)
/// cannot be detected statically; review such scripts by hand.
/// </para>
/// </summary>
internal sealed class UnattendedAzureScanner(string root)
{
    // az/azd/docker/bicep (and the other container tools') invocations, any spelling, flags before the verb, are judged by CliInvocations.
    private static readonly Regex[] Forbidden =
    [
        Pattern(@"sqlpackage.*Publish"),
        // The azd hooks, by either slash direction.
        Pattern(@"infra[\\/]+hooks"),
        // The Azure CLI started as a Python module.
        Pattern(@"(?<![\w-])-m\s+azure\.cli\b"),
        // Any Az PowerShell cmdlet or alias (Connect-AzAccount, Login-AzAccount, Get-AzAccessToken, ...) and AzureRM.
        // Case-sensitive so `Import-Module Az` and ordinary words stay clean.
        Pattern(@"\b[A-Za-z]+-(?-i:Az[A-Z][A-Za-z]*|AzureRm[A-Z][A-Za-z]*)\b"),
        // Azure endpoints: ARM (not an ARM template's $schema URL), Entra tokens, storage, Key Vault, SQL, App Service
        // (incl. scm/Kudu), and the US Government and China clouds.
        Pattern(@"(?<!schema\.)\bmanagement\.azure\.com\b"),
        Pattern(@"\blogin\.microsoftonline\.com\b"),
        Pattern(@"\bcore\.windows\.net\b"),
        Pattern(@"\bvault\.azure\.net\b"),
        Pattern(@"\bdatabase\.windows\.net\b"),
        Pattern(@"\bazurewebsites\.net\b"),
        Pattern(@"\busgovcloudapi\.net\b"),
        Pattern(@"\bchinacloudapi\.cn\b"),
        // Registry pushes by other container tools (docker, docker-compose, podman, nerdctl and buildah are tokenised).
        Pattern(@"\bskopeo\s+(?:copy|sync)\b"),
        Pattern(@"\boras\s+(?:push|cp|copy|attach)\b"),
        Pattern(@"\bcrane\s+(?:push|copy|cp)\b"),
        Pattern(@"\bctr\b[^;&|]*?\bimages?\s+push\b"),
        // .NET SDK container publishing (pushes when a registry is set).
        Pattern(@"\bPublishContainer\b"),
        Pattern(@"[-/]p(?:roperty)?:ContainerRegistry="),
        // Builds that push as part of the build, whichever tool or (continuation) line carries the flag.
        Pattern(@"(?<![\w-])--push(?![\w-])"),
        Pattern(@"\btype=registry\b"),
        Pattern(@"(?<![\w-])push=true\b"),
        // Registry access without a push command: logging in to any registry, or referencing an ACR.
        Pattern(@"\buses\s*:\s*['""]?docker/login-action"),
        Pattern(@"\bazurecr\.io\b"),
        // Reusable workflows from another repository (a local ./.github/workflows/x.yml stays allowed).
        Pattern(@"\buses\s*:\s*['""]?(?!\./)[^'""\s@]+/[^'""\s@]+[\\/]\.github[\\/]workflows[\\/]"),
        // Azure's own actions (login, arm-deploy, sql-action, container-apps-deploy-action, ...) and the
        // action that pushes images; either can reach Azure or a registry without a shell command.
        Pattern(@"\buses\s*:\s*['""]?azure/"),
        Pattern(@"\buses\s*:\s*['""]?docker/build-push-action"),
    ];

    // For JavaScript/JSON under .github/scripts: commands that are unambiguous even inside a string.
    private static readonly Regex[] Unambiguous =
    [
        Pattern(@"\bazd(?:\.exe)?\s+(?:up|provision|deploy|down|hooks|auth|init|pipeline|env)\b"),
        Pattern(@"\bdocker\s+(?:(?:image|manifest)\s+)?push\b"),
        Pattern(@"\bazurecr\.io\b"),
        Pattern(@"(?<!schema\.)\bmanagement\.azure\.com\b"),
        Pattern(@"\b[A-Za-z]+-(?-i:Az[A-Z][A-Za-z]*|AzureRm[A-Z][A-Za-z]*)\b"),
    ];

    private static readonly Regex KeyLine = new(
        @"^\s*(?:-\s*)?(?:""(?<key>[^""]+)""|'(?<key>[^']+)'|(?<key>[\w.-]+))\s*:(?<rest>.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex RunKey = new(@"^(?<lead>\s*(?:-\s+)?)run\s*:(?<value>.*)$", RegexOptions.CultureInvariant);

    private static readonly Regex BlockScalarIndicator = new(@"^[|>][-+0-9]*$", RegexOptions.CultureInvariant);

    private static readonly Regex ListEntry = new(
        @"^\s*-\s*(?:""(?<key>[^""]+)""|'(?<key>[^']+)'|(?<key>[\w.-]+))\s*$",
        RegexOptions.CultureInvariant);

    public IReadOnlyList<ScanHit> Scan()
    {
        var hits = new List<ScanHit>();
        foreach (var file in EnumerateFiles())
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var rules = RulesFor(relative);
            if (rules == FileRules.Skip)
            {
                continue;
            }

            var lines = File.ReadAllText(file).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            if (rules == FileRules.Unambiguous)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    hits.AddRange(Unambiguous.Where(p => p.IsMatch(lines[i])).Select(p => new ScanHit(relative, i + 1, p.ToString())));
                }

                continue;
            }

            var exempt = IsWorkflow(relative) ? ExemptLines(lines) : [];

            foreach (var statement in Statements(lines, IsYaml(relative)))
            {
                // A statement is exempt only if every physical line of it is.
                if (Enumerable.Range(statement.First, statement.Last - statement.First + 1).All(exempt.Contains))
                {
                    continue;
                }

                foreach (var pattern in Forbidden)
                {
                    if (pattern.IsMatch(statement.Text))
                    {
                        hits.Add(new ScanHit(relative, statement.First + 1, pattern.ToString()));
                    }
                }

                foreach (var rule in CliInvocations.Violations(statement.Text))
                {
                    hits.Add(new ScanHit(relative, statement.First + 1, rule));
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Logical statements: physical lines joined across shell (<c>\</c>) and PowerShell (<c>`</c>) line
    /// continuations, so <c>docker buildx build \</c> / <c>--push</c> is matched as one command. <c>#</c> comments
    /// are stripped so a mention in prose is not a hit, unless some line of the file leaves a quote open: a
    /// multi-line string makes it impossible to tell a comment from string content, so nothing is stripped.
    /// In YAML a trailing backtick continues only inside <c>run:</c> content: elsewhere (a step <c>name:</c> such
    /// as <c>Install `azd`</c>) it is Markdown, and joining it to the next key would invent a command. The lines
    /// of a folded <c>run: &gt;</c> block are one shell line once YAML folds them, so they form one statement; a
    /// literal <c>|</c> block keeps one command per line.
    /// </summary>
    private static IEnumerable<Statement> Statements(string[] lines, bool isYaml)
    {
        var stripComments = !lines.Any(l => ScanLine(l).UnterminatedQuote);
        var run = isYaml ? RunLines(lines) : null;
        var text = new System.Text.StringBuilder();
        var first = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var code = (stripComments ? ScanLine(lines[i]).Code : lines[i]).TrimEnd();
            var folded = run is not null && run.FoldedBlock[i] >= 0 && i + 1 < lines.Length && run.FoldedBlock[i + 1] == run.FoldedBlock[i];
            var lineContinues = code.EndsWith('\\') || (code.EndsWith('`') && (run?.Content[i] ?? true));
            var continues = i + 1 < lines.Length && (folded || lineContinues);
            text.Append(lineContinues && continues ? code[..^1] + " " : folded ? code + " " : code);
            if (continues)
            {
                continue;
            }

            yield return new Statement(first, i, text.ToString());
            text.Clear();
            first = i + 1;
        }
    }

    /// <summary>Physical lines [<paramref name="First"/>, <paramref name="Last"/>] joined into one statement.</summary>
    private sealed record Statement(int First, int Last, string Text);

    /// <summary>
    /// The lines of a YAML file that hold script code: a <c>run:</c> line itself and, when its value is a block
    /// scalar (<c>|</c>, <c>&gt;-</c>, ...), every following line indented deeper than the <c>run</c> key. Lines of a
    /// FOLDED (<c>&gt;</c>) block carry the index of their <c>run:</c> line in <see cref="RunLineInfo.FoldedBlock"/>
    /// (-1 otherwise).
    /// </summary>
    private static RunLineInfo RunLines(string[] lines)
    {
        var content = new bool[lines.Length];
        var folded = Enumerable.Repeat(-1, lines.Length).ToArray();
        int? blockKeyIndent = null;
        var blockStart = -1;
        var blockFolds = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (blockKeyIndent is int keyIndent)
            {
                if (line.Trim().Length == 0 || IndentOf(line) > keyIndent)
                {
                    content[i] = true;
                    folded[i] = blockFolds ? blockStart : -1;
                    continue;
                }

                blockKeyIndent = null;
            }

            var run = RunKey.Match(line);
            if (!run.Success)
            {
                continue;
            }

            content[i] = true;
            var indicator = StripComment(run.Groups["value"].Value).Trim();
            if (BlockScalarIndicator.IsMatch(indicator))
            {
                blockKeyIndent = run.Groups["lead"].Length;
                blockStart = i;
                blockFolds = indicator.StartsWith('>');
            }
        }

        return new RunLineInfo(content, folded);
    }

    private sealed record RunLineInfo(bool[] Content, int[] FoldedBlock);

    private static Regex Pattern(string expression) =>
        new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsWorkflow(string relative) =>
        relative.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) && IsYaml(relative);

    private static bool IsYaml(string relative) =>
        relative.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

    // .github/scripts is scanned because workflows run code from there (docs-validate.yml on push).
    private IEnumerable<string> EnumerateFiles()
    {
        foreach (var relative in new[] { ".github/workflows", ".github/actions", ".github/scripts", "eng" })
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

    /// <summary>Which rules apply to a file; only <c>.github/scripts</c> narrows them (see the class comment).</summary>
    private static FileRules RulesFor(string relative)
    {
        if (!relative.StartsWith(".github/scripts/", StringComparison.OrdinalIgnoreCase))
        {
            return FileRules.All;
        }

        var directories = relative.Split('/')[..^1];
        if (directories.Contains("node_modules", StringComparer.OrdinalIgnoreCase) || directories.Contains("fixtures", StringComparer.OrdinalIgnoreCase))
        {
            return FileRules.Skip;
        }

        return Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".md" => FileRules.Skip,
            ".js" or ".mjs" or ".cjs" or ".ts" or ".json" => FileRules.Unambiguous,
            _ => FileRules.All,
        };
    }

    private enum FileRules
    {
        All,
        Unambiguous,
        Skip,
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
    /// <c>name:</c> of a mapping. A step input that happens to be called environment does not count, and a job
    /// with more than one <c>environment</c> key (quoted or not) is never exempt: parsers disagree on which wins.
    /// </summary>
    private static bool DeclaresAzureLive(string[] lines, int start, int end)
    {
        int? childIndent = null;
        var environments = 0;
        var azureLive = false;
        for (var i = start + 1; i < end; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            childIndent ??= IndentOf(line);
            if (IndentOf(line) != childIndent || line.TrimStart().StartsWith('-'))
            {
                continue;
            }

            var key = KeyLine.Match(line);
            if (!key.Success || key.Groups["key"].Value != "environment")
            {
                continue;
            }

            environments++;
            var value = key.Groups["rest"].Value.Trim().Trim('"', '\'');
            azureLive = value == "azure-live" || (value.Length == 0 && MappingNamesAzureLive(lines, i, end, childIndent.Value));
        }

        return environments == 1 && azureLive;
    }

    /// <summary>True when the block mapping under the <c>environment:</c> key at <paramref name="keyLine"/> has <c>name: azure-live</c>.</summary>
    private static bool MappingNamesAzureLive(string[] lines, int keyLine, int end, int keyIndent)
    {
        for (var j = keyLine + 1; j < end; j++)
        {
            var next = StripComment(lines[j]);
            if (next.Trim().Length == 0)
            {
                continue;
            }

            if (IndentOf(next) <= keyIndent)
            {
                return false;
            }

            if (Regex.IsMatch(next, @"^\s*name\s*:\s*['""]?azure-live['""]?\s*$"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A line that could be part of a multi-line quoted scalar (unterminated quote), a multi-line flow collection
    /// (unbalanced <c>[</c>/<c>{</c>), or a YAML complex key; each can hide structure from this line-oriented reader.
    /// </summary>
    private static bool IsUnsafeToInterpret(string line)
    {
        var scan = ScanLine(line);
        var code = scan.Code.Trim();
        return scan.UnterminatedQuote
            || scan.UnbalancedBrackets
            || code == "?"
            || code.StartsWith("? ", StringComparison.Ordinal);
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static string StripComment(string line) => ScanLine(line).Code;

    /// <summary>
    /// Reads one line quote-aware: the code before a trailing <c>#</c> comment, whether a quote is left open at
    /// line end, and whether <c>[</c>/<c>{</c> and <c>]</c>/<c>}</c> outside quotes fail to balance.
    /// </summary>
    private static LineScan ScanLine(string line)
    {
        char? quote = null;
        var depth = 0;
        var underflow = false;
        var end = line.Length;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is null)
            {
                if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c is '[' or '{')
                {
                    depth++;
                }
                else if (c is ']' or '}')
                {
                    underflow |= --depth < 0;
                }
                else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                {
                    end = i;
                    break;
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

        return new LineScan(line[..end], quote is not null, underflow || depth != 0);
    }

    private readonly record struct LineScan(string Code, bool UnterminatedQuote, bool UnbalancedBrackets);
}
