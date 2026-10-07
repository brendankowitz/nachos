namespace Nachos.Infra.Tests;

/// <summary>
/// Offline Bicep compilation. Prefers <c>az bicep</c> (what CI has) and falls back to the standalone
/// <c>bicep</c> binary. Only <c>build</c>/<c>build-params</c> are ever invoked: no deployment, no what-if.
/// </summary>
internal static class BicepCli
{
    public static ProcessResult Build(string bicepFile)
    {
        var (exe, prefix) = Resolve();
        return prefix.Length > 0
            ? Tools.Run(exe, [.. prefix, "build", "--file", bicepFile, "--stdout"])
            : Tools.Run(exe, ["build", bicepFile, "--stdout"]);
    }

    public static ProcessResult BuildParams(string bicepparamFile)
    {
        var (exe, prefix) = Resolve();
        return prefix.Length > 0
            ? Tools.Run(exe, [.. prefix, "build-params", "--file", bicepparamFile, "--stdout"])
            : Tools.Run(exe, ["build-params", bicepparamFile, "--stdout"]);
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
