using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>
/// Static taint check of a hook script: which variables hold key or secret material (value or file path), and
/// which statements print one of them. Line-oriented and deliberately conservative (it errs towards reporting).
/// <para>
/// Sources: the output of <c>keys create</c>, random key generation (<c>/dev/urandom</c>,
/// <c>RandomNumberGenerator</c>), <c>secret download</c>, and the <c>NACHOS_SIGNING_SECRET</c> hand-off variable.
/// Taint flows through assignments, redirections (<c>&gt;"$file"</c>), <c>--file</c> and <c>WriteAllText</c> until
/// nothing changes (including <c>local</c>/<c>export</c>/<c>declare</c>/<c>readonly</c> and <c>read</c>). A print is
/// <c>echo</c>/<c>printf</c>/<c>cat</c>/<c>tee</c> (also after <c>&gt;&amp;2</c>; not when redirected to a file),
/// <c>Write-*</c>, <c>[Console]::Write*</c>, <c>throw</c>, <c>Get-Content</c> (format-operator arguments included),
/// or a bare PowerShell expression of a tainted variable that is not piped to <c>Out-Null</c>.
/// </para>
/// </summary>
internal static partial class HookSecretFlow
{
    [GeneratedRegex(@"keys create|/dev/urandom|RandomNumberGenerator|secret download", RegexOptions.IgnoreCase)]
    private static partial Regex Source();

    // $name, ${name}, ${name#...}, $env:NAME, ${env:NAME}
    [GeneratedRegex(@"\$\{?(?:env:)?(?<v>[A-Za-z_]\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    // NAME=... at the start of a statement (also an env prefix such as `NAME="$(...)" cmd`, and after
    // local/export/declare/readonly/typeset), or $name = / $env:NAME =
    [GeneratedRegex(
        @"^\s*(?:(?:local|export|declare|readonly|typeset)(?:\s+-\w+)*\s+)?(?:\$(?:env:)?)?(?<v>[A-Za-z_]\w*)\s*\+?=(?!=)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();

    // read [-r ...] NAME: the variable receives whatever the statement reads (a tainted file, a here-string).
    [GeneratedRegex(@"\bread\b(?:\s+-\w+)*\s+(?<v>[A-Za-z_]\w*)")]
    private static partial Regex ReadTarget();

    // >"$file", >> $file, --file "$file", WriteAllText($file, ...)
    [GeneratedRegex(@"(?:(?<!&)>{1,2}\s*""?|--file\s+""?|WriteAllText\(\s*)\$\{?(?:env:)?(?<v>[A-Za-z_]\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex FileTarget();

    // Also after leading redirections such as `>&2 echo` / `1>&2 echo`.
    [GeneratedRegex(@"(?:^|[;&|{]\s*|\b(?:then|do|else)\s+)(?:\d*>&\d+\s+)*(?<cmd>echo|printf|cat|tee)\b(?<args>[^;&|]*)")]
    private static partial Regex ShellPrint();

    // Stdout sent to a file (not >&2): the material goes to a temp file, which is the intended path.
    [GeneratedRegex(@"(?<!&)>{1,2}\s*""?\$")]
    private static partial Regex RedirectToFile();

    [GeneratedRegex(
        @"(?:^|[;{(]\s*|\|\s*)(?<cmd>Write-Host|Write-Output|Write-Information|Write-Verbose|Write-Debug|Write-Warning|Write-Error|throw|Get-Content|Out-Host|echo|cat|type|\[Console\]::(?:Error\.)?Write(?:Line)?)\b(?<args>[^;]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellPrint();

    // Output explicitly thrown away: `... | Out-Null`, `... > $null`.
    [GeneratedRegex(@"\|\s*Out-Null\b|>\s*\$null\b", RegexOptions.IgnoreCase)]
    private static partial Regex Discarded();

    // A statement that is only an expression: `$x`, `$x.Trim()`, `"...$x..."`.
    [GeneratedRegex(@"^\s*(?:\$\{?(?:env:)?(?<v>[A-Za-z_]\w*)\}?\s*(?:$|[.|\[])|""[^""]*\$)", RegexOptions.IgnoreCase)]
    private static partial Regex BareExpression();

    public static IReadOnlySet<string> SensitiveNames(string script)
    {
        var statements = Statements(script);
        var tainted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NACHOS_SIGNING_SECRET" };
        bool changed;
        do
        {
            changed = false;
            foreach (var statement in statements)
            {
                if (!Source().IsMatch(statement) && !References(statement).Any(tainted.Contains))
                {
                    continue;
                }

                foreach (var target in Targets(statement))
                {
                    changed |= tainted.Add(target);
                }
            }
        }
        while (changed);

        return tainted;
    }

    public static IReadOnlyList<string> Leaks(string script, bool powerShell)
    {
        var tainted = SensitiveNames(script);
        var leaks = new List<string>();
        foreach (var statement in Statements(script))
        {
            var prints = powerShell
                ? PowerShellPrint().Matches(statement).Select(m => m.Groups["args"].Value)
                : ShellPrint().Matches(statement).Select(m => m.Groups["args"].Value).Where(a => !RedirectToFile().IsMatch(a));
            var printed = prints.Any(args => References(args).Any(tainted.Contains));

            if (powerShell && !printed)
            {
                var bare = BareExpression().Match(statement);
                printed = bare.Success && !Discarded().IsMatch(statement) && References(statement).Any(tainted.Contains);
            }

            if (printed)
            {
                leaks.Add(statement.Trim());
            }
        }

        return leaks;
    }

    private static IEnumerable<string> References(string text) => Reference().Matches(text).Select(m => m.Groups["v"].Value);

    private static IEnumerable<string> Targets(string statement) =>
        Assignment().Matches(statement).Concat(FileTarget().Matches(statement)).Concat(ReadTarget().Matches(statement))
            .Select(m => m.Groups["v"].Value);

    /// <summary>Statements with comments removed and <c>\</c> / <c>`</c> line continuations joined.</summary>
    private static List<string> Statements(string script) =>
        Regex.Replace(script.Replace("\r", string.Empty, StringComparison.Ordinal), @"(\\|`)[ \t]*\n[ \t]*", " ")
            .Split('\n')
            .Select(StripComment)
            .Where(l => l.Trim().Length > 0)
            .ToList();

    /// <summary>Drops a <c>#</c> comment that starts the line or follows whitespace outside quotes.</summary>
    private static string StripComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is null && c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == quote)
            {
                quote = null;
            }
            else if (quote is null && c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }

        return line;
    }
}
