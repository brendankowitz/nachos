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
/// PowerShell line continuations, YAML folded <c>run: &gt;</c> blocks and multi-line plain or quoted YAML scalars
/// joined, <c>#</c> comments stripped when quoting is unambiguous) and reported at the statement's first physical
/// line. The continuation lines of a display-only YAML key (<c>name</c>, <c>run-name</c>, <c>description</c>) are prose
/// and not scanned.
/// <para>
/// <c>.github/scripts</c> also holds JavaScript, docs and test fixtures, where words like <c>az</c> are ordinary
/// identifiers or prose. There, <c>node_modules/</c> (created by <c>npm ci</c>), <c>fixtures/</c> and <c>*.md</c>
/// are skipped, and <c>*.js|mjs|cjs|ts|json</c> are checked only for unambiguous commands (<c>azd</c> with a
/// deploying verb, <c>docker push</c>, ACR and ARM endpoints, Az cmdlets). Limit: JavaScript that shells out to
/// <c>az</c> through a string is detected only when the string holds such an unambiguous command. Every other
/// file there, and every file under the other roots (including any tracked <c>node_modules</c>), gets all rules,
/// except that outside <c>.github/scripts</c> a <c>*.md</c> file is prose and gets only the unambiguous rules.
/// </para>
/// <para>
/// Markdown outside <c>.github/scripts</c> is read as prose because its sentences ("offline Bicep validation, and SDK
/// conformance") read as <c>bicep &lt;unknown verb&gt;</c> to the command allow-list. It still flags the unambiguous
/// commands above; it does NOT flag an ambiguous <c>az &lt;verb&gt;</c> or <c>bicep &lt;verb&gt;</c> command. That is
/// safe only while Markdown is never executed, so the scripts that could execute it (YAML <c>run:</c> content and
/// shell/PowerShell scripts under the scanned roots) are checked for running a <c>*.md</c> file as code: given to an
/// interpreter (bash, sh, zsh, dash, pwsh, powershell, python*, node, source, <c>.</c>, iex, Invoke-Expression, eval),
/// piped or redirected into one, or invoked as a command (<see cref="MarkdownExecution"/>). Naming a Markdown file
/// any other way (<c>cat README.md</c>, <c>markdownlint docs/*.md</c>, a trigger path or action input) is not flagged.
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

    // A YAML `key:` (optionally a sequence entry's first key) followed by whitespace or the end; its column is lead's length.
    private static readonly Regex KeyNode = new(
        @"^(?<lead>\s*(?:-\s+)?)(?:""(?<key>[^""]*)""|'(?<key>[^']*)'|(?<key>[\w.-]+))\s*:(?:\s+(?<value>.*))?$",
        RegexOptions.CultureInvariant);

    // A YAML sequence entry `- value` (not a `- key:`, which KeyNode reads first); its column is the dash's.
    private static readonly Regex EntryNode = new(@"^(?<lead>\s*)-(?:\s+(?<value>.*))?$", RegexOptions.CultureInvariant);

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
            var yaml = IsYaml(relative) ? ReadYamlLines(lines) : null;
            var shellScript = yaml is null && IsShellScript(relative);

            foreach (var statement in Statements(lines, yaml))
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

                // Only script code runs a file: run: content in YAML (not a trigger path, step name or action input that
                // names a *.md), and every line of a shell or PowerShell script.
                var isScript = yaml?.Script[statement.First] ?? shellScript;
                if (isScript && MarkdownExecution.RunsMarkdown(yaml is null ? statement.Text : WithoutRunKey(statement.Text)))
                {
                    hits.Add(new ScanHit(relative, statement.First + 1, MarkdownExecution.Rule));
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
    /// as <c>Install `azd`</c>) it is Markdown, and joining it to the next key would invent a command. Lines that
    /// YAML folds into one line (<see cref="ReadYamlLines"/>) form one statement; a literal <c>|</c> block keeps one
    /// command per line, and the continuation lines of a display-only key are not scanned.
    /// </summary>
    private static IEnumerable<Statement> Statements(string[] lines, YamlLines? yaml)
    {
        var stripComments = !lines.Any(l => ScanLine(l).UnterminatedQuote);
        var text = new System.Text.StringBuilder();
        var first = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var code = yaml?.Prose[i] == true ? string.Empty : (stripComments ? ScanLine(lines[i]).Code : lines[i]).TrimEnd();
            var folded = yaml is not null && yaml.Fold[i] >= 0 && i + 1 < lines.Length && yaml.Fold[i + 1] == yaml.Fold[i];
            var lineContinues = code.EndsWith('\\') || (code.EndsWith('`') && (yaml?.Script[i] ?? true));
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
    /// How YAML reads each line, for every <c>key:</c> or <c>- </c> entry whose value is a scalar:
    /// <list type="bullet">
    /// <item>A block scalar (<c>|</c>, <c>&gt;-</c>, ...) owns every following line indented deeper than its key; those
    /// lines are content, never keys. Only a folded (<c>&gt;</c>) <c>run:</c> block is joined, as before; other block
    /// lines stay one statement each.</item>
    /// <item>A plain or single/double-quoted scalar continues on lines indented deeper than its key (a plain one stops at
    /// a <c>key:</c> line, a quoted one at its closing quote; the value may also start on the line after an empty
    /// <c>key:</c>). YAML joins those lines with a single space and turns a blank line into a newline, so the non-blank
    /// lines between blank lines share a <see cref="YamlLines.Fold"/> group.</item>
    /// <item>The continuation lines of a display-only key (<c>name</c>, <c>run-name</c>, <c>description</c>) are
    /// <see cref="YamlLines.Prose"/>: never a command, so not scanned. The key line itself is scanned as before.</item>
    /// </list>
    /// <see cref="YamlLines.Script"/> marks <c>run:</c> content: the key line and every line of its value.
    /// </summary>
    private static YamlLines ReadYamlLines(string[] lines)
    {
        var layout = new YamlLines(new bool[lines.Length], Enumerable.Repeat(-1, lines.Length).ToArray(), new bool[lines.Length]);
        var i = 0;
        while (i < lines.Length)
        {
            i = ReadNode(lines, i, layout);
        }

        return layout;
    }

    /// <summary>Records the node starting at <paramref name="line"/> in <paramref name="layout"/>; returns the next line to read.</summary>
    private static int ReadNode(string[] lines, int line, YamlLines layout)
    {
        var key = KeyNode.Match(lines[line]);
        var node = key.Success ? key : EntryNode.Match(lines[line]);
        if (!node.Success)
        {
            return line + 1;
        }

        var column = node.Groups["lead"].Length;
        var name = key.Success ? key.Groups["key"].Value : null;
        var isRun = name == "run";
        var isProse = name is "name" or "run-name" or "description";
        layout.Script[line] |= isRun;

        var first = line;
        var valueAt = node.Groups["value"].Success ? node.Groups["value"].Index : lines[line].Length;
        if (StripComment(lines[line][valueAt..]).Trim().Length == 0)
        {
            // No value on the key line: a scalar may start on the next one (`run:` then a deeper `azd`).
            first = NextCodeLine(lines, line + 1);
            if (first < 0 || IndentOf(lines[first]) <= column || KeyNode.IsMatch(lines[first]) || EntryNode.IsMatch(lines[first]))
            {
                return line + 1; // a nested mapping or sequence, or nothing
            }

            valueAt = IndentOf(lines[first]);
        }

        var value = StripComment(lines[first][valueAt..]).Trim();
        if (BlockScalarIndicator.IsMatch(value))
        {
            var end = first + 1;
            while (end < lines.Length && (lines[end].Trim().Length == 0 || IndentOf(lines[end]) > column))
            {
                layout.Script[end] = isRun;
                layout.Fold[end] = isRun && value.StartsWith('>') ? first : -1;
                end++;
            }

            return end;
        }

        int last;
        if (value[0] is '"' or '\'')
        {
            last = QuotedScalarEnd(lines, first, valueAt, column);
        }
        else if (value[0] is '[' or '{' or '&' or '*' or '!' or '%' or '@' or '`' or '|' or '>' || value is "-" or "?" || value.StartsWith("- ", StringComparison.Ordinal) || value.StartsWith("? ", StringComparison.Ordinal))
        {
            return first + 1; // flow collection, anchor, alias, tag or other indicator: not read as a scalar
        }
        else
        {
            last = PlainScalarEnd(lines, first, column);
        }

        for (var k = first; k <= last; k++)
        {
            if (isProse)
            {
                layout.Prose[k] = k != line;
                continue;
            }

            layout.Script[k] |= isRun;
            layout.Fold[k] = lines[k].Trim().Length == 0 ? -1 : first;
        }

        return last + 1;
    }

    /// <summary>The last line of a plain scalar starting on <paramref name="first"/> under a key at <paramref name="column"/>.</summary>
    private static int PlainScalarEnd(string[] lines, int first, int column)
    {
        var last = first;
        for (var k = first + 1; k < lines.Length; k++)
        {
            if (lines[k].Trim().Length == 0)
            {
                continue;
            }

            if (IndentOf(lines[k]) <= column || KeyNode.IsMatch(lines[k]))
            {
                break; // a plain scalar cannot hold `key: `, so that line is the next node
            }

            last = k;
        }

        return last;
    }

    /// <summary>
    /// The last line of a quoted scalar opening at <paramref name="quoteAt"/> on <paramref name="first"/>: the line
    /// that closes it, or the last deeper line before the indentation falls back to the key's (malformed YAML).
    /// </summary>
    private static int QuotedScalarEnd(string[] lines, int first, int quoteAt, int column)
    {
        var quote = lines[first][quoteAt];
        if (ClosingQuote(lines[first], quoteAt + 1, quote) >= 0)
        {
            return first;
        }

        var last = first;
        for (var k = first + 1; k < lines.Length; k++)
        {
            if (lines[k].Trim().Length == 0)
            {
                continue;
            }

            if (IndentOf(lines[k]) <= column)
            {
                break;
            }

            last = k;
            if (ClosingQuote(lines[k], 0, quote) >= 0)
            {
                break;
            }
        }

        return last;
    }

    /// <summary>The index of the quote closing a YAML scalar (<c>\"</c> escapes in double quotes, <c>''</c> in single), or -1.</summary>
    private static int ClosingQuote(string text, int from, char quote)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote && quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The first line at or after <paramref name="from"/> that is not blank or a comment, or -1.</summary>
    private static int NextCodeLine(string[] lines, int from)
    {
        for (var i = from; i < lines.Length; i++)
        {
            if (StripComment(lines[i]).Trim().Length > 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <param name="Script">The line is <c>run:</c> content.</param>
    /// <param name="Fold">Lines with the same non-negative group are one line once YAML folds them (-1: not folded).</param>
    /// <param name="Prose">The line continues a display-only key's value.</param>
    private sealed record YamlLines(bool[] Script, int[] Fold, bool[] Prose);

    /// <summary>A statement's command line without the YAML <c>run:</c> key it starts with, if any.</summary>
    private static string WithoutRunKey(string statement)
    {
        var run = RunKey.Match(statement);
        return run.Success ? run.Groups["value"].Value : statement;
    }

    /// <summary>Shell and PowerShell scripts outside YAML (an extensionless file may be an executable script).</summary>
    private static bool IsShellScript(string relative) =>
        Path.GetExtension(relative).ToLowerInvariant() is "" or ".sh" or ".bash" or ".zsh" or ".ksh" or ".dash" or ".ps1" or ".psm1" or ".cmd" or ".bat";

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

    /// <summary>
    /// Which rules apply to a file. Under <c>.github/scripts</c> the rules narrow by file type (see the class comment).
    /// Elsewhere every file gets all rules except <c>*.md</c>, which is prose and gets only the unambiguous rules: an
    /// ambiguous <c>az</c>/<c>bicep</c> command written only in Markdown is not detected. Executing Markdown is what
    /// would make that a gap, and is flagged in the scripts that could do it (<see cref="MarkdownExecution"/>).
    /// </summary>
    private static FileRules RulesFor(string relative)
    {
        if (!relative.StartsWith(".github/scripts/", StringComparison.OrdinalIgnoreCase))
        {
            return relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? FileRules.Unambiguous : FileRules.All;
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
