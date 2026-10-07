using Shouldly;

namespace Nachos.Infra.Tests;

/// <summary>
/// The hooks must never print key or secret material. Which variables hold it is derived from the scripts
/// themselves (what `keys create`, the random generator and `secret download` produce, and everything assigned
/// or written from those), so a renamed variable is still covered.
/// </summary>
public sealed class HookSecretFlowTests
{
    private static readonly string[] Hooks =
    [
        "infra/hooks/postprovision.sh",
        "infra/hooks/postprovision.ps1",
        "infra/hooks/postdeploy.sh",
        "infra/hooks/postdeploy.ps1",
    ];

    [Fact]
    public void Hooks_NeverPrintKeyOrSecretMaterial()
    {
        foreach (var hook in Hooks)
        {
            HookSecretFlow.Leaks(Read(hook), IsPowerShell(hook)).ShouldBeEmpty(hook);
        }
    }

    [Theory]
    [InlineData("infra/hooks/postprovision.sh", "minted raw_admin_file admin_file signing_file NACHOS_SIGNING_SECRET")]
    [InlineData("infra/hooks/postprovision.ps1", "minted adminKey adminFile signingFile bytes encoded NACHOS_SIGNING_SECRET")]
    public void SensitiveNames_AreDerivedFromTheScript(string hook, string expected)
    {
        var names = HookSecretFlow.SensitiveNames(Read(hook));

        foreach (var name in expected.Split(' '))
        {
            names.ShouldContain(name, $"{hook}: '{name}' holds key/secret material");
        }
    }

    [Theory]
    [InlineData("infra/hooks/postprovision.sh", "echo \"$minted\"")]
    [InlineData("infra/hooks/postprovision.sh", "echo \"key: ${minted}\" >&2")]
    [InlineData("infra/hooks/postprovision.sh", "cat \"$signing_file\"")]
    [InlineData("infra/hooks/postprovision.sh", "printf '%s\\n' \"$NACHOS_SIGNING_SECRET\"")]
    [InlineData("infra/hooks/postprovision.sh", "[[ -n \"$minted\" ]] && echo \"$minted\"")]
    [InlineData("infra/hooks/postprovision.ps1", "Write-Output $adminKey")]
    [InlineData("infra/hooks/postprovision.ps1", "Write-Host \"key=$minted\"")]
    [InlineData("infra/hooks/postprovision.ps1", "$adminKey")]
    [InlineData("infra/hooks/postprovision.ps1", "throw \"bad key: $adminKey\"")]
    [InlineData("infra/hooks/postprovision.ps1", "Get-Content $signingFile")]
    [InlineData("infra/hooks/postprovision.ps1", "Write-Verbose $env:NACHOS_SIGNING_SECRET")]
    [InlineData("infra/hooks/postprovision.sh", ">&2 echo \"$minted\"")]
    [InlineData("infra/hooks/postprovision.sh", "1>&2 echo \"$minted\"")]
    [InlineData("infra/hooks/postprovision.sh", "local copy=\"$minted\"\necho \"$copy\"")]
    [InlineData("infra/hooks/postprovision.sh", "export ADMIN=\"$minted\"\nprintf '%s' \"$ADMIN\"")]
    [InlineData("infra/hooks/postprovision.sh", "declare -r kept=\"$minted\"\necho \"$kept\"")]
    [InlineData("infra/hooks/postprovision.sh", "readonly kept=\"$minted\"\necho \"$kept\"")]
    [InlineData("infra/hooks/postprovision.sh", "read -r line <\"$admin_file\"\necho \"$line\"")]
    [InlineData("infra/hooks/postprovision.ps1", "[Console]::WriteLine($adminKey)")]
    [InlineData("infra/hooks/postprovision.ps1", "[Console]::Error.WriteLine($minted)")]
    [InlineData("infra/hooks/postprovision.ps1", "Write-Host (\"{0}\" -f $adminKey)")]
    public void PlantedPrints_AreFound(string hook, string planted)
    {
        var text = Read(hook).TrimEnd() + "\n" + planted + "\n";

        HookSecretFlow.Leaks(text, IsPowerShell(hook)).ShouldNotBeEmpty(planted);
    }

    [Theory]
    // Writing the material to a temp file (for `secret set --file`) is the intended path, not a leak.
    [InlineData("infra/hooks/postprovision.sh", "printf '%s' \"$minted\" >\"$admin_file\"")]
    [InlineData("infra/hooks/postprovision.sh", "echo \"stored '$vault'\"")]
    [InlineData("infra/hooks/postprovision.ps1", "Write-Output \"postprovision: stored secret 'nachos-bootstrap-admin-key'.\"")]
    [InlineData("infra/hooks/postprovision.ps1", "if ($adminKey -cnotmatch 'x') { throw 'no key' }")]
    [InlineData("infra/hooks/postprovision.ps1", "$minted | Out-Null")]
    [InlineData("infra/hooks/postprovision.sh", "local status=$?\necho \"exit $status\"")]
    public void WritesToFilesAndNamesOnly_AreNotLeaks(string hook, string planted)
    {
        var text = Read(hook).TrimEnd() + "\n" + planted + "\n";

        HookSecretFlow.Leaks(text, IsPowerShell(hook)).ShouldBeEmpty(planted);
    }

    private static bool IsPowerShell(string hook) => hook.EndsWith(".ps1", StringComparison.Ordinal);

    private static string Read(string hook) => File.ReadAllText(RepoPaths.Combine(hook));
}
