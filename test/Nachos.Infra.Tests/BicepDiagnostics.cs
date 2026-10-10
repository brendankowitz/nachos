using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>
/// Recognises Bicep compiler/linter diagnostics in stderr (<c>file(1,2) : Warning no-unused-params: ...</c>,
/// <c>Error BCP036: ...</c>). The wrapper <c>az</c> adds unrelated stderr noise (for example "a new Bicep
/// release is available"), so tests assert on diagnostics, not on empty stderr.
/// </summary>
internal static partial class BicepDiagnostics
{
    [GeneratedRegex(@":\s*(Warning|Error)\s+[A-Za-z0-9-]+\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticLine();

    public static IReadOnlyList<string> Find(string stderr) =>
        stderr.Split('\n').Where(line => DiagnosticLine().IsMatch(line)).Select(line => line.Trim()).ToList();
}
