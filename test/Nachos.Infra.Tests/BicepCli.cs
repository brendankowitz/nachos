using System.Globalization;
using System.Text.RegularExpressions;

namespace Nachos.Infra.Tests;

/// <summary>
/// Offline Bicep compilation. Prefers <c>az bicep</c> (what CI has) and falls back to the standalone
/// <c>bicep</c> binary. Only <c>build</c>/<c>build-params</c>/<c>version</c> are ever invoked: no deployment, no what-if.
/// </summary>
internal static class BicepCli
{
    // `az` otherwise checks for (and prints) new Bicep releases, which is noise and a network call.
    private static readonly Dictionary<string, string> Environment = new() { ["AZURE_BICEP_CHECK_VERSION"] = "false" };

    public static ProcessResult Build(string bicepFile)
    {
        var (exe, prefix) = Resolve();
        return prefix.Length > 0
            ? Tools.Run(exe, [.. prefix, "build", "--file", bicepFile, "--stdout"], environment: Environment)
            : Tools.Run(exe, ["build", bicepFile, "--stdout"], environment: Environment);
    }

    public static ProcessResult BuildParams(string bicepparamFile)
    {
        var (exe, prefix) = Resolve();
        return prefix.Length > 0
            ? Tools.Run(exe, [.. prefix, "build-params", "--file", bicepparamFile, "--stdout"], environment: Environment)
            : Tools.Run(exe, ["build-params", bicepparamFile, "--stdout"], environment: Environment);
    }

    /// <summary>
    /// The Bicep the templates were validated with. Older Bicep lacks type data for the newest API versions used
    /// (Microsoft.App@2026-01-01, Microsoft.Sql@2025-01-01, ...) and reports BCP081 for each of them.
    /// </summary>
    public static Version MinimumVersion { get; } = new(0, 48, 1);

    /// <summary>The installed Bicep CLI version (<c>az bicep version</c> or <c>bicep --version</c>).</summary>
    public static Version Version()
    {
        var (exe, prefix) = Resolve();
        var result = prefix.Length > 0
            ? Tools.Run(exe, [.. prefix, "version"], environment: Environment)
            : Tools.Run(exe, ["--version"], environment: Environment);
        var match = Regex.Match(result.StdOut + result.StdErr, @"(\d+)\.(\d+)\.(\d+)");
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : throw new InvalidOperationException($"Could not read the Bicep version from: {result.StdOut}{result.StdErr}");
    }

    /// <summary>A failure message for <paramref name="diagnostics"/>, naming an outdated Bicep when it explains them.</summary>
    public static string Explain(IReadOnlyList<string> diagnostics, Version version)
    {
        var list = string.Join(System.Environment.NewLine, diagnostics);
        return version < MinimumVersion && diagnostics.Any(d => d.Contains("BCP081", StringComparison.Ordinal))
            ? $"Bicep {version} is older than the {MinimumVersion} type data these API versions need; upgrade Bicep.{System.Environment.NewLine}{list}"
            : $"bicep must be warning-free:{System.Environment.NewLine}{list}";
    }

    private static (string Exe, string[] Prefix) Resolve()
    {
        var az = Tools.Find("az");
        if (az is not null)
        {
            return (az, ["bicep"]);
        }

        return (Tools.Require("bicep"), []);
    }
}
