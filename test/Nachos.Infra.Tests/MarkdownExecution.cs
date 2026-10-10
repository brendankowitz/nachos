using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>
/// Finds a command line that runs a Markdown (<c>*.md</c>, any case) file as code. <see cref="UnattendedAzureScanner"/>
/// reads Markdown as prose, which is safe only while CI never executes it; a script that did would hide its commands.
/// Command words are found after <c>;</c>, <c>&amp;</c>, <c>|</c>, parentheses, braces and backticks, past variable
/// assignments, shell keywords (<c>then</c>, <c>do</c>, ...), redirects and the <c>env</c>/<c>sudo</c>/<c>exec</c>/<c>command</c>
/// wrappers. Reported:
/// <list type="bullet">
/// <item>bash, sh, zsh, dash, pwsh, powershell, python*, node, <c>source</c> or <c>.</c> (by name, path or quoted) whose
/// script operand is a Markdown path, or, with no operand, whose <c>&lt;</c> input is one or whose standard input
/// carries one down a pipeline; an inline script (<c>bash -c</c>, <c>pwsh -Command</c>) is read as a command line in
/// turn, and one built by substitution (<c>bash -c "$(cat x.md)"</c>, <c>bash &lt;(cat x.md)</c>) counts when it names
/// a Markdown path;</item>
/// <item>iex, Invoke-Expression or eval naming a Markdown path in its arguments, or fed one down a pipeline;</item>
/// <item>a Markdown path as the command word itself (<c>./deploy.md</c>, <c>&amp; ./deploy.md</c>).</item>
/// </list>
/// Markdown flows down a pipeline from cat, tee, Get-Content, gc or type given a Markdown path, or from any command
/// with a <c>&lt;</c> redirect from one. A Markdown path anywhere else (<c>cat README.md</c>, <c>markdownlint docs/*.md</c>,
/// data after a script operand as in <c>node render.mjs README.md</c>, a module run as <c>python -m markdown README.md</c>)
/// is not reported. Limits: separators are found without regard to quoting; a Markdown path held in a variable, or a
/// script operand after an unknown option that takes a path value, is not seen.
/// </summary>
internal static class MarkdownExecution
{
    public const string Rule =
        "Markdown run as a script (*.md given to bash/sh/zsh/dash/pwsh/powershell/python/node/source/./iex/Invoke-Expression/eval, " +
        "piped or redirected into one, or run as a command): Markdown is never executed by CI and is scanned only as prose, " +
        "so move these commands into a .sh or .ps1 script under a scanned root";

    // Command boundaries. `&` of a redirect (2>&1, &>, >&) is not one.
    private static readonly Regex Separator = new(@"\|\||&&|(?<![<>])&(?!>)|[;|(){}`]", RegexOptions.CultureInvariant);

    // A shell word: quoted runs stay whole (`"C:\Program Files\x"`, `"source x.md"`).
    private static readonly Regex Token = new(@"(?:""[^""]*""|'[^']*'|[^\s""']+)+", RegexOptions.CultureInvariant);

    private static readonly Regex Assignment = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant);

    private static readonly Regex Redirect = new(@"^\d*(?<op><<<|<<|<&|<|>>|>&|>|&>>|&>)(?<target>.*)$", RegexOptions.CultureInvariant);

    private static readonly Regex Python = new(@"^python[\d.]*$", RegexOptions.CultureInvariant);

    // A bash/sh short-option cluster that includes -c (`-c`, `-ec`, `-xc`): the next word is the script.
    private static readonly Regex ShellInlineScript = new(@"^-[A-Za-z]*c[A-Za-z]*$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Keywords = new(["if", "then", "else", "elif", "do", "while", "until", "time", "!"], StringComparer.Ordinal);

    private static readonly HashSet<string> Readers = new(["cat", "tee", "get-content", "gc", "type"], StringComparer.Ordinal);

    // Wrappers that run the rest of their arguments as a command, with the options that take a value.
    private static readonly Dictionary<string, HashSet<string>> Wrappers = new(StringComparer.Ordinal)
    {
        ["env"] = new(["-u", "--unset", "-C", "--chdir"], StringComparer.Ordinal),
        ["sudo"] = new(["-u", "--user", "-g", "--group", "-C", "--close-from", "-D", "--chdir", "-h", "--host", "-p", "--prompt", "-r", "--role", "-t", "--type", "-U", "--other-user", "-T", "--command-timeout"], StringComparer.Ordinal),
        ["exec"] = new(["-a"], StringComparer.Ordinal),
        ["command"] = new(StringComparer.Ordinal),
    };

    private enum Family
    {
        Shell,
        PowerShell,
        Python,
        Node,
        Source,
        Eval,
    }

    private enum Role
    {
        Switch,
        Value,
        InlineScript,
        ScriptPath,
        Module,
    }

    private enum Operand
    {
        Markdown,
        Other,
        StandardInput,
    }

    /// <summary>True when <paramref name="commandLine"/> runs a Markdown file as code.</summary>
    public static bool RunsMarkdown(string commandLine)
    {
        var markdownFlows = false; // Markdown text is flowing down the current pipeline
        var start = 0;
        var before = string.Empty;
        foreach (var separator in Separator.Matches(commandLine).Cast<Match?>().Append(null))
        {
            var end = separator?.Index ?? commandLine.Length;
            if (before is ";" or "&" or "&&" or "||")
            {
                markdownFlows = false;
            }

            if (RunsMarkdown(commandLine, start, end, ref markdownFlows))
            {
                return true;
            }

            if (separator is not null)
            {
                start = separator.Index + separator.Length;
                before = separator.Value;
            }
        }

        return false;
    }

    /// <summary>Judges the command in <paramref name="commandLine"/>[<paramref name="start"/>..<paramref name="end"/>].</summary>
    private static bool RunsMarkdown(string commandLine, int start, int end, ref bool markdownFlows)
    {
        var words = Token.Matches(commandLine[start..end]);
        var tokens = words.Select(w => w.Value).ToList();
        var markdownInput = false;
        var word = CommandWord(tokens, ref markdownInput);
        markdownInput |= ReadsMarkdownInput(tokens);
        if (word < 0)
        {
            markdownFlows |= markdownInput;
            return false;
        }

        if (IsMarkdown(tokens[word]))
        {
            return true;
        }

        var name = ProgramName(tokens[word]);
        if (InterpreterFamily(name) is Family family)
        {
            // The interpreter's arguments run to the end of its command, through any substitution it contains.
            var argumentsAt = start + words[word].Index + words[word].Length;
            var arguments = Token.Matches(ArgumentSpan(commandLine, argumentsAt)).Select(m => m.Value).ToList();
            if (family == Family.Eval)
            {
                return markdownFlows || arguments.Any(IsMarkdown);
            }

            return ScriptOperand(arguments, family) switch
            {
                Operand.Markdown => true,
                Operand.StandardInput => markdownFlows || markdownInput,
                _ => false,
            };
        }

        markdownFlows |= markdownInput || (Readers.Contains(name) && tokens.Skip(word + 1).Any(IsMarkdown));
        return false;
    }

    /// <summary>The index of the command word, past assignments, keywords, redirects and wrappers; -1 when there is none.</summary>
    private static int CommandWord(List<string> tokens, ref bool markdownInput)
    {
        var i = 0;
        while (i < tokens.Count)
        {
            if (Assignment.IsMatch(tokens[i]) || Keywords.Contains(tokens[i]))
            {
                i++;
                continue;
            }

            if (SkipRedirect(tokens, ref i, ref markdownInput))
            {
                continue;
            }

            var wrapper = ProgramName(tokens[i]);
            if (!Wrappers.TryGetValue(wrapper, out var valueOptions))
            {
                return i;
            }

            i++;
            while (i < tokens.Count && (tokens[i].StartsWith('-') || Assignment.IsMatch(tokens[i])))
            {
                if (wrapper == "command" && tokens[i] is "-v" or "-V")
                {
                    return -1; // `command -v bash` looks the program up; it does not run it
                }

                i += valueOptions.Contains(tokens[i]) ? 2 : 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads the interpreter's arguments up to its script operand: a Markdown one, another one (a script, module,
    /// inline script or substitution that names no Markdown), or none, in which case it reads its script from standard
    /// input (also when given <c>-</c>).
    /// </summary>
    private static Operand ScriptOperand(List<string> arguments, Family family)
    {
        var markdownInput = false;
        var i = 0;
        while (i < arguments.Count)
        {
            var argument = arguments[i];
            if (argument.StartsWith("<(", StringComparison.Ordinal) || argument.StartsWith("$(", StringComparison.Ordinal) || argument.StartsWith('('))
            {
                return arguments.Skip(i).Any(IsMarkdown) ? Operand.Markdown : Operand.Other; // a script produced by a substitution
            }

            if (SkipRedirect(arguments, ref i, ref markdownInput))
            {
                continue;
            }

            var bare = argument.Trim('"', '\'');
            if (bare == "-")
            {
                break;
            }

            if (bare.StartsWith('-') || (family == Family.Shell && bare.StartsWith('+')))
            {
                switch (RoleOf(family, bare))
                {
                    case Role.InlineScript:
                        return InlineScriptRunsMarkdown(arguments.Skip(i + 1).ToList(), family) ? Operand.Markdown : Operand.Other;
                    case Role.ScriptPath:
                        return i + 1 < arguments.Count && IsMarkdown(arguments[i + 1]) ? Operand.Markdown : Operand.Other;
                    case Role.Module:
                        return Operand.Other;
                    case Role.Value:
                        i += 2;
                        continue;
                    default:
                        i++;
                        continue;
                }
            }

            if (IsMarkdown(bare))
            {
                return Operand.Markdown;
            }

            if (bare.StartsWith('$') || bare is "." or ".." || bare.Contains('/') || bare.Contains('\\') || Path.HasExtension(bare))
            {
                return Operand.Other; // a script path, or one held in a variable
            }

            i++; // a bare word: the value of an unknown option, or a program on PATH
        }

        return markdownInput ? Operand.Markdown : Operand.StandardInput;
    }

    /// <summary>
    /// <c>bash -c "..."</c> and <c>pwsh -Command ...</c>: a script built by substitution counts when it names a Markdown
    /// path; a literal one is read as a command line. PowerShell joins every remaining argument into the script.
    /// </summary>
    private static bool InlineScriptRunsMarkdown(List<string> rest, Family family)
    {
        if (rest.Count == 0)
        {
            return false;
        }

        var script = (family == Family.PowerShell ? string.Join(' ', rest) : rest[0]).Trim('"', '\'');
        return script.StartsWith("$(", StringComparison.Ordinal) || script.StartsWith('`')
            ? IsMarkdown(script) || Token.Matches(script).Any(m => IsMarkdown(m.Value))
            : RunsMarkdown(script);
    }

    private static Role RoleOf(Family family, string option) => family switch
    {
        Family.Shell when ShellInlineScript.IsMatch(option) => Role.InlineScript,
        Family.Shell => option is "-o" or "+o" or "-O" or "+O" or "--rcfile" or "--init-file" ? Role.Value : Role.Switch,
        Family.Python => option switch
        {
            "-c" => Role.InlineScript,
            "-m" => Role.Module,
            "-W" or "-X" => Role.Value,
            _ => Role.Switch,
        },
        Family.Node => option switch
        {
            "-e" or "--eval" or "-p" or "--print" => Role.InlineScript,
            "-r" or "--require" or "--import" or "--loader" or "--experimental-loader" or "-C" or "--conditions" or "--input-type" or "--env-file" => Role.Value,
            _ => Role.Switch,
        },
        // PowerShell parameters are case-insensitive. -EncodedCommand cannot be read, so it is treated like a module.
        Family.PowerShell => option.ToLowerInvariant() switch
        {
            "-c" or "-command" or "-cwa" or "-commandwithargs" => Role.InlineScript,
            "-f" or "-file" => Role.ScriptPath,
            "-e" or "-ec" or "-encodedcommand" => Role.Module,
            "-ex" or "-ep" or "-executionpolicy" or "-wd" or "-workingdirectory" or "-o" or "-of" or "-outputformat" or "-if" or "-inputformat"
                or "-config" or "-configurationname" or "-settingsfile" or "-w" or "-windowstyle" or "-custompipename" or "-configurationfile" => Role.Value,
            _ => Role.Switch,
        },
        _ => Role.Switch,
    };

    private static Family? InterpreterFamily(string name) => name switch
    {
        "bash" or "sh" or "zsh" or "dash" => Family.Shell,
        "pwsh" or "powershell" => Family.PowerShell,
        "node" => Family.Node,
        "source" or "." => Family.Source,
        "iex" or "invoke-expression" or "eval" => Family.Eval,
        _ when Python.IsMatch(name) => Family.Python,
        _ => null,
    };

    /// <summary>
    /// Skips a redirect at <paramref name="i"/> (with its target, when that is the next word); a <c>&lt;</c> from a
    /// Markdown path sets <paramref name="markdownInput"/>. False when the word is not a redirect.
    /// </summary>
    private static bool SkipRedirect(List<string> tokens, ref int i, ref bool markdownInput)
    {
        var redirect = Redirect.Match(tokens[i]);
        if (!redirect.Success)
        {
            return false;
        }

        var target = redirect.Groups["target"].Value;
        var separate = target.Length == 0;
        if (separate && i + 1 < tokens.Count)
        {
            target = tokens[i + 1];
        }

        markdownInput |= redirect.Groups["op"].Value == "<" && IsMarkdown(target);
        i += separate ? 2 : 1;
        return true;
    }

    private static bool ReadsMarkdownInput(List<string> tokens)
    {
        var markdownInput = false;
        for (var i = 0; i < tokens.Count;)
        {
            if (!SkipRedirect(tokens, ref i, ref markdownInput))
            {
                i++;
            }
        }

        return markdownInput;
    }

    /// <summary>
    /// The command's arguments from <paramref name="from"/> to the end of its command: a <c>;</c>, <c>&amp;</c>,
    /// <c>|</c> or backtick outside parentheses, or a <c>)</c> that closes a parenthesis opened before it.
    /// </summary>
    private static string ArgumentSpan(string commandLine, int from)
    {
        var depth = 0;
        for (var i = from; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && (c is ')' or '`' or ';' or '|' || (c == '&' && !IsRedirectAmpersand(commandLine, i))))
            {
                return commandLine[from..i];
            }
        }

        return commandLine[from..];
    }

    private static bool IsRedirectAmpersand(string text, int i) =>
        (i > 0 && text[i - 1] is '<' or '>') || (i + 1 < text.Length && text[i + 1] == '>');

    /// <summary>The program a command word names: unquoted, without its directory or <c>.exe</c>, lower case.</summary>
    private static string ProgramName(string word)
    {
        var name = word.Trim('"', '\'');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private static bool IsMarkdown(string word)
    {
        var path = word.Trim('"', '\'', '(', ')', '`', ';', ',', '<', '>', '$');
        return path.Length > 3 && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }
}
