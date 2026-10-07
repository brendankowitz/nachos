using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace Nachos.Infra.Tests;

/// <summary>
/// Offline validation of the Azure deployment assets (spec §18.2, §18.3). Nothing here talks to Azure:
/// Bicep is only compiled, hook scripts are only syntax-checked or statically scanned.
/// </summary>
public sealed class InfraTests
{
    private const string MainBicep = "infra/main.bicep";

    private static readonly string[] HookFiles =
    [
        "infra/hooks/postprovision.sh",
        "infra/hooks/postprovision.ps1",
        "infra/hooks/postdeploy.sh",
        "infra/hooks/postdeploy.ps1",
    ];

    // Compiled once and shared: every ARM-shape assertion reads the same template.
    private static readonly Lazy<ProcessResult> Compiled = new(() => BicepCli.Build(RepoPaths.Combine(MainBicep)));

    // ---- Bicep compilation -------------------------------------------------------------------------

    [RequiresToolFact("az", "bicep")]
    public void Bicep_Builds()
    {
        var result = Compiled.Value;

        result.ExitCode.ShouldBe(0, result.StdErr);
        // The brief demands zero diagnostics (unknown API versions, lint rules): build emits them all on stderr.
        result.StdErr.Trim().ShouldBeEmpty("bicep build must be warning-free");
    }

    [RequiresToolFact("az", "bicep")]
    public void Bicep_Parameters_Build()
    {
        using var template = ArmTemplate();
        var declared = template.RootElement.GetProperty("parameters");
        var required = declared.EnumerateObject()
            .Where(p => !p.Value.TryGetProperty("defaultValue", out _) && !p.Value.TryGetProperty("nullable", out _))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        using var parameters = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("infra/main.parameters.json")));
        var supplied = parameters.RootElement.GetProperty("parameters").EnumerateObject().Select(p => p.Name).ToList();

        supplied.Where(name => !declared.TryGetProperty(name, out _))
            .ShouldBeEmpty("main.parameters.json names a parameter main.bicep does not declare");
        required.Where(name => !supplied.Contains(name))
            .ShouldBeEmpty("main.parameters.json omits a required parameter");

        // azd substitutes ${VARS} at provision time, which bicep cannot see. Re-express the same parameter
        // set as a .bicepparam with sample values so bicep itself type-checks it against main.bicep.
        var directory = Directory.CreateTempSubdirectory("nachos-infra-").FullName;
        try
        {
            var bicepparam = Path.Combine(directory, "sample.bicepparam");
            var relativeMain = Path.GetRelativePath(directory, RepoPaths.Combine(MainBicep)).Replace('\\', '/');
            var lines = new List<string> { $"using '{relativeMain}'" };
            lines.AddRange(supplied.Select(name => $"param {name} = {SampleValue(name)}"));
            File.WriteAllLines(bicepparam, lines);

            var result = BicepCli.BuildParams(bicepparam);

            result.ExitCode.ShouldBe(0, result.StdErr);
            result.StdErr.Trim().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- Shape of the compiled template ------------------------------------------------------------

    [RequiresToolFact("az", "bicep")]
    public void Sql_IsEntraOnly()
    {
        using var template = ArmTemplate();

        var servers = Resources(template, "Microsoft.Sql/servers").ToList();
        servers.ShouldNotBeEmpty();
        foreach (var server in servers)
        {
            var administrators = server.GetProperty("properties").GetProperty("administrators");
            administrators.GetProperty("azureADOnlyAuthentication").ValueKind.ShouldBe(JsonValueKind.True);
            administrators.GetProperty("administratorType").GetString().ShouldBe("ActiveDirectory");
        }

        // No SQL-auth credential may exist anywhere in the template (Entra-only, spec §18.2).
        var names = PropertyNames(template.RootElement).ToList();
        names.ShouldNotContain("administratorLogin");
        names.ShouldNotContain("administratorLoginPassword");
    }

    [RequiresToolFact("az", "bicep")]
    public void Sql_Database_IsServerlessWithAutoPauseDisabled()
    {
        using var template = ArmTemplate();

        var database = Resources(template, "Microsoft.Sql/servers/databases").ShouldHaveSingleItem();
        database.GetProperty("sku").GetProperty("name").GetString().ShouldBe("GP_S_Gen5");
        database.GetProperty("properties").GetProperty("autoPauseDelay").GetInt32().ShouldBe(-1);
    }

    [RequiresToolFact("az", "bicep")]
    public void Container_App_IsExternalMinOne_WithProbes()
    {
        using var template = ArmTemplate();

        var app = Resources(template, "Microsoft.App/containerApps").ShouldHaveSingleItem();
        var properties = app.GetProperty("properties");

        properties.GetProperty("configuration").GetProperty("ingress").GetProperty("external").ValueKind.ShouldBe(JsonValueKind.True);

        var containerTemplate = properties.GetProperty("template");
        containerTemplate.GetProperty("scale").GetProperty("minReplicas").GetInt32().ShouldBe(1);

        var probes = containerTemplate.GetProperty("containers").EnumerateArray()
            .SelectMany(c => c.GetProperty("probes").EnumerateArray())
            .ToDictionary(
                p => p.GetProperty("type").GetString()!,
                p => p.GetProperty("httpGet").GetProperty("path").GetString()!,
                StringComparer.Ordinal);
        probes["Liveness"].ShouldBe("/health/live");
        probes["Readiness"].ShouldBe("/health/ready");
    }

    [RequiresToolFact("az", "bicep")]
    public void KeyVault_IsRbac()
    {
        using var template = ArmTemplate();

        var vault = Resources(template, "Microsoft.KeyVault/vaults").ShouldHaveSingleItem();
        vault.GetProperty("properties").GetProperty("enableRbacAuthorization").ValueKind.ShouldBe(JsonValueKind.True);
    }

    // ---- azure.yaml and hooks ----------------------------------------------------------------------

    [Fact]
    public void AzureYaml_ServicesPointAtExistingProjects()
    {
        var yaml = File.ReadAllText(RepoPaths.Combine("azure.yaml"));

        var projects = Regex.Matches(yaml, @"^\s*project:\s*(\S+)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        projects.ShouldNotBeEmpty("azure.yaml declares no service project");
        foreach (var project in projects)
        {
            var directory = RepoPaths.Combine(project);
            Directory.Exists(directory).ShouldBeTrue($"service project '{project}' does not exist");
            Directory.EnumerateFiles(directory, "*.csproj").ShouldNotBeEmpty($"'{project}' has no .csproj");
        }

        var hooks = Regex.Matches(yaml, @"^\s*run:\s*(\S+)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        hooks.ShouldNotBeEmpty("azure.yaml declares no hooks");
        foreach (var hook in hooks)
        {
            File.Exists(RepoPaths.Combine(hook)).ShouldBeTrue($"hook '{hook}' does not exist");
        }
    }

    [RequiresToolFact("bash")]
    public void Hooks_ParseWithoutSyntaxErrors_Bash()
    {
        var bash = Tools.Require("bash");
        foreach (var hook in HookFiles.Where(h => h.EndsWith(".sh", StringComparison.Ordinal)))
        {
            var result = Tools.Run(bash, ["-n", RepoPaths.Combine(hook)]);
            result.ExitCode.ShouldBe(0, $"{hook}: {result.StdErr}");
        }
    }

    [RequiresToolFact("pwsh")]
    public void Hooks_ParseWithoutSyntaxErrors_PowerShell()
    {
        var pwsh = Tools.Require("pwsh");
        const string Script =
            "$errors = $null; $tokens = $null; " +
            "[void][System.Management.Automation.Language.Parser]::ParseFile($env:NACHOS_HOOK, [ref]$tokens, [ref]$errors); " +
            "if ($errors.Count -gt 0) { $errors | ForEach-Object { $_.ToString() }; exit 1 }";

        foreach (var hook in HookFiles.Where(h => h.EndsWith(".ps1", StringComparison.Ordinal)))
        {
            var result = Tools.Run(
                pwsh,
                ["-NoProfile", "-NonInteractive", "-Command", Script],
                environment: new Dictionary<string, string> { ["NACHOS_HOOK"] = RepoPaths.Combine(hook) });
            result.ExitCode.ShouldBe(0, $"{hook}: {result.StdOut}{result.StdErr}");
        }
    }

    [Fact]
    public void Hooks_NeverEchoSecrets()
    {
        // Writing or interpolating a variable whose name says it holds a secret/key value into any output stream.
        var output = new Regex(
            @"\b(echo|printf|Write-Host|Write-Output|Write-Information|Write-Verbose|Write-Debug)\b.*\$\{?[\w:]*(SECRET|KEY_VALUE)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var hook in HookFiles)
        {
            File.Exists(RepoPaths.Combine(hook)).ShouldBeTrue($"{hook} is missing");
            var text = File.ReadAllText(RepoPaths.Combine(hook));

            foreach (var (line, index) in text.Split('\n').Select((l, i) => (l, i)))
            {
                output.IsMatch(line).ShouldBeFalse($"{hook}:{index + 1} echoes a secret-named variable");
            }

            // A secret on the command line shows up in process listings; Key Vault writes must go through --file.
            text.ShouldNotContain("--value", customMessage: $"{hook} passes a secret value as an argument");
            text.ShouldNotContain("set -x", customMessage: $"{hook} enables command tracing");
            text.ShouldNotContain("Set-PSDebug", customMessage: $"{hook} enables command tracing");
            if (text.Contains("keyvault secret set", StringComparison.Ordinal))
            {
                text.ShouldContain("--file", customMessage: $"{hook} writes secrets without --file");
            }
        }
    }

    // ---- Spec §18.3: no unattended Azure path ------------------------------------------------------

    [Fact]
    public void Repo_HasNoUnattendedAzurePath()
    {
        var hits = new UnattendedAzureScanner(RepoPaths.Root).Scan();

        hits.ShouldBeEmpty(string.Join(Environment.NewLine, hits.Select(h => $"{h.File}:{h.Line} matches /{h.Pattern}/")));
    }

    [Fact]
    public void Repo_HasNoUnattendedAzurePath_DetectsPlantedAzdUp()
    {
        const string OnPush = "name: x\non: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n      - run: azd up\n";
        PlantedWorkflowHits(OnPush).ShouldNotBeEmpty("azd up on push must be flagged");

        const string DispatchOnly = "name: x\non: workflow_dispatch\njobs:\n  j:\n    runs-on: ubuntu-latest\n    environment: azure-live\n    steps:\n      - run: azd up\n";
        PlantedWorkflowHits(DispatchOnly).ShouldBeEmpty("workflow_dispatch + azure-live is the one allowed path");

        const string FlowList = "name: x\non: [workflow_dispatch]\njobs:\n  j:\n    environment: azure-live\n    steps:\n      - run: azd up\n";
        PlantedWorkflowHits(FlowList).ShouldBeEmpty();

        const string Block = "name: x\non:\n  workflow_dispatch:\n    inputs:\n      a:\n        type: string\njobs:\n  j:\n    environment:\n      name: azure-live\n    steps:\n      - run: azd up\n";
        PlantedWorkflowHits(Block).ShouldBeEmpty();

        const string NoEnvironment = "name: x\non: workflow_dispatch\njobs:\n  j:\n    steps:\n      - run: azd up\n";
        PlantedWorkflowHits(NoEnvironment).ShouldNotBeEmpty("workflow_dispatch without azure-live must be flagged");

        const string MixedTriggers = "name: x\non:\n  workflow_dispatch:\n  push:\njobs:\n  j:\n    environment: azure-live\n    steps:\n      - run: azd up\n";
        PlantedWorkflowHits(MixedTriggers).ShouldNotBeEmpty("workflow_dispatch plus push must be flagged");

        const string EngScript = "az group create -n x -l y\n";
        PlantedFileHits("eng/deploy.sh", EngScript).ShouldNotBeEmpty("eng scripts are never exempt");
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static IReadOnlyList<ScanHit> PlantedWorkflowHits(string workflow) =>
        PlantedFileHits(".github/workflows/x.yml", workflow);

    private static IReadOnlyList<ScanHit> PlantedFileHits(string relativePath, string content)
    {
        var root = Directory.CreateTempSubdirectory("nachos-scan-").FullName;
        try
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return new UnattendedAzureScanner(root).Scan();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonDocument ArmTemplate()
    {
        var result = Compiled.Value;
        result.ExitCode.ShouldBe(0, result.StdErr);
        return JsonDocument.Parse(result.StdOut);
    }

    /// <summary>All resources of an ARM type, including those inside nested module deployments.</summary>
    private static IEnumerable<JsonElement> Resources(JsonDocument template, string type) =>
        Descendants(template.RootElement).Where(e =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("type", out var t)
            && t.ValueKind == JsonValueKind.String
            && t.GetString() == type
            && e.TryGetProperty("apiVersion", out _));

    private static IEnumerable<JsonElement> Descendants(JsonElement element)
    {
        yield return element;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var child in Descendants(property.Value))
                    {
                        yield return child;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in Descendants(item))
                    {
                        yield return child;
                    }
                }

                break;
        }
    }

    private static IEnumerable<string> PropertyNames(JsonElement root) =>
        Descendants(root)
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .SelectMany(e => e.EnumerateObject().Select(p => p.Name));

    private static string SampleValue(string parameter) => parameter switch
    {
        "environmentName" => "'nachos-test'",
        "location" => "'australiaeast'",
        "principalId" => "'00000000-0000-0000-0000-000000000001'",
        "principalLogin" => "'dev@example.com'",
        "openAiEndpoint" => "''",
        "principalType" => "'User'",
        _ => throw new InvalidOperationException($"Add a sample value for parameter '{parameter}' to SampleValue()."),
    };
}
