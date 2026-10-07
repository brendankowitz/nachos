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

    private static readonly string[] PostprovisionHooks =
    [
        "infra/hooks/postprovision.sh",
        "infra/hooks/postprovision.ps1",
    ];

    // Compiled once and shared: every ARM-shape assertion reads the same template.
    private static readonly Lazy<ProcessResult> Compiled = new(() => BicepCli.Build(RepoPaths.Combine(MainBicep)));

    // ---- Bicep compilation -------------------------------------------------------------------------

    [RequiresToolFact("az", "bicep")]
    public void Bicep_Builds()
    {
        var result = Compiled.Value;

        result.ExitCode.ShouldBe(0, result.StdErr);
        // Zero diagnostics (unknown API versions, lint rules): build emits them all on stderr. Other stderr
        // noise from the `az` wrapper is not a Bicep diagnostic and is ignored.
        BicepDiagnostics.Find(result.StdErr).ShouldBeEmpty("bicep build must be warning-free");
    }

    [RequiresToolFact("az", "bicep")]
    public void Bicep_Diagnostics_AreStillDetected()
    {
        var directory = Directory.CreateTempSubdirectory("nachos-bicep-").FullName;
        try
        {
            var file = Path.Combine(directory, "planted.bicep");
            File.WriteAllText(file, "param unusedThing string = ''\n");

            var result = BicepCli.Build(file);

            BicepDiagnostics.Find(result.StdErr).ShouldNotBeEmpty("an unused parameter must surface as a lint warning");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
            var relativeMain = Path.GetRelativePath(directory, RepoPaths.Combine(MainBicep)).Replace('\\', '/');

            // apiExists flips between the first provision (false) and every later one (true).
            foreach (var apiExists in new[] { "false", "true" })
            {
                var bicepparam = Path.Combine(directory, $"sample-{apiExists}.bicepparam");
                var lines = new List<string> { $"using '{relativeMain}'" };
                lines.AddRange(supplied.Select(name => $"param {name} = {SampleValue(name, apiExists)}"));
                File.WriteAllLines(bicepparam, lines);

                var result = BicepCli.BuildParams(bicepparam);

                result.ExitCode.ShouldBe(0, result.StdErr);
                BicepDiagnostics.Find(result.StdErr).ShouldBeEmpty();
            }
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
        var properties = database.GetProperty("properties");
        properties.GetProperty("autoPauseDelay").GetInt32().ShouldBe(-1);
        NumberOrJsonLiteral(properties.GetProperty("minCapacity")).ShouldBe(0.5);
    }

    [RequiresToolFact("az", "bicep")]
    public void Container_App_IsExternalMinOne_WithProbes()
    {
        using var template = ArmTemplate();

        var app = Resources(template, "Microsoft.App/containerApps").ShouldHaveSingleItem();
        var properties = app.GetProperty("properties");

        var ingress = properties.GetProperty("configuration").GetProperty("ingress");
        ingress.GetProperty("external").ValueKind.ShouldBe(JsonValueKind.True);
        ingress.GetProperty("allowInsecure").ValueKind.ShouldBe(JsonValueKind.False);
        ResolveInt(template, ingress.GetProperty("targetPort")).ShouldBe(8080);

        var containerTemplate = properties.GetProperty("template");
        var scale = containerTemplate.GetProperty("scale");
        scale.GetProperty("minReplicas").GetInt32().ShouldBe(1);
        scale.GetProperty("maxReplicas").GetInt32().ShouldBe(10);

        // The probes are an ARM expression keyed on the image actually in use, NOT on apiExists: azd sets
        // apiExists as soon as the app exists, which can be before any real image was deployed (a failed
        // postprovision, or a second `azd provision`). The placeholder has no /health routes.
        var container = containerTemplate.GetProperty("containers").EnumerateArray().Single();
        var probes = container.GetProperty("probes");
        probes.ValueKind.ShouldBe(JsonValueKind.String, "probes must be conditional on the image in use");
        var expression = probes.GetString()!;
        // Probes apply when the image in use (the container's own image expression) differs from the placeholder.
        var imageExpression = container.GetProperty("image").GetString()!;
        imageExpression.ShouldStartWith("[");
        expression.ShouldStartWith($"[if(not(equals({imageExpression[1..^1]}, parameters('containerImage'))), ");
        expression.ShouldEndWith(", createArray())]");
        expression.ShouldContain("'/health/live'");
        expression.ShouldContain("'/health/ready'");
        expression.ShouldContain("'Liveness'");
        expression.ShouldContain("'Readiness'");
        expression.ShouldContain("variables('targetPort')");
    }

    [RequiresToolFact("az", "bicep")]
    public void Container_App_Image_IsAspNetPlaceholder_AndKeepsDeployedImageWhenItExists()
    {
        using var template = ArmTemplate();

        // The placeholder must listen on the target port (8080); the old helloworld image listens on 80.
        var defaults = Descendants(template.RootElement)
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("containerImage", out var p) && p.TryGetProperty("defaultValue", out _))
            .Select(e => e.GetProperty("containerImage").GetProperty("defaultValue").GetString())
            .ToList();
        defaults.ShouldBe(["mcr.microsoft.com/dotnet/samples:aspnetapp"]);

        var app = Resources(template, "Microsoft.App/containerApps").ShouldHaveSingleItem();
        var image = app.GetProperty("properties").GetProperty("template").GetProperty("containers").EnumerateArray().Single()
            .GetProperty("image").GetString()!;
        image.ShouldStartWith("[if(parameters('apiExists'),");
        image.ShouldContain("parameters('containerImage')");

        // The existing app's image is read by a deployment that only runs when the app already exists.
        Descendants(template.RootElement)
            .Where(e => e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("type", out var t) && t.GetString() == "Microsoft.Resources/deployments"
                && e.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.String
                && c.GetString()!.Contains("apiExists", StringComparison.Ordinal))
            .ShouldNotBeEmpty("a conditional fetch-container-image deployment is required");
    }

    [Fact]
    public void Parameters_MapAzdValues_ForApiExistsAndPrincipalType()
    {
        using var parameters = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("infra/main.parameters.json")));
        var values = parameters.RootElement.GetProperty("parameters");

        // azd sets SERVICE_API_RESOURCE_EXISTS after the first deploy; the default covers the first provision.
        values.GetProperty("apiExists").GetProperty("value").GetString().ShouldBe("${SERVICE_API_RESOURCE_EXISTS=false}");
        values.GetProperty("principalType").GetProperty("value").GetString().ShouldBe("${AZURE_PRINCIPAL_TYPE=User}");
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

    [Fact]
    public void Hooks_DotnetRun_NeverUsesLaunchProfile()
    {
        // A launch profile makes `dotnet run` print a banner on stdout (corrupting the captured admin key) and
        // injects profile environment variables into the CLI.
        foreach (var hook in PostprovisionHooks)
        {
            var runs = LogicalLines(hook).Where(l => l.Contains("dotnet run", StringComparison.Ordinal)).ToList();
            runs.ShouldNotBeEmpty($"{hook} has no dotnet run");
            runs.Where(l => !l.Contains("--no-launch-profile", StringComparison.Ordinal))
                .ShouldBeEmpty($"{hook}: every dotnet run needs --no-launch-profile");
        }
    }

    [Fact]
    public void Hooks_ValidateAdminKeyShape_AndAbortBeforeStoringIt()
    {
        // Comment lines are stripped so prose cannot satisfy the checks.
        var sh = CodeOnly("infra/hooks/postprovision.sh");
        var mismatch = Regex.Match(sh, @"=~ \$jwt_shape \]\]; then(?:(?!\bfi\b).)*?exit 1", RegexOptions.Singleline);
        mismatch.Success.ShouldBeTrue("postprovision.sh must exit when the key shape does not match");
        mismatch.Index.ShouldBeLessThan(sh.LastIndexOf("--name nachos-bootstrap-admin-key", StringComparison.Ordinal));

        var ps1 = CodeOnly("infra/hooks/postprovision.ps1");
        var throws = Regex.Match(ps1, @"-cnotmatch '\^\[A-Za-z0-9_-\]\+\\\.\[A-Za-z0-9_-\]\+\\\.\[A-Za-z0-9_-\]\+\$'\)\s*\{\s*throw");
        throws.Success.ShouldBeTrue("postprovision.ps1 must throw when the key shape does not match");
        throws.Index.ShouldBeLessThan(ps1.LastIndexOf("--name nachos-bootstrap-admin-key", StringComparison.Ordinal));
    }

    [Fact]
    public void Hooks_PinTheSubscription_OnEveryAzCall()
    {
        foreach (var hook in PostprovisionHooks)
        {
            ReadHook(hook).ShouldContain("AZURE_SUBSCRIPTION_ID", customMessage: $"{hook} must require the subscription id");

            var azCalls = LogicalLines(hook)
                .Where(l => Regex.IsMatch(l, @"(^|[\s(=])az\s+(sql|keyvault)\s", RegexOptions.CultureInvariant))
                .ToList();
            azCalls.Count.ShouldBeGreaterThanOrEqualTo(4, $"{hook}: expected firewall create/delete and Key Vault calls");
            azCalls.Where(l => !l.Contains("--subscription", StringComparison.Ordinal))
                .ShouldBeEmpty($"{hook}: az must not fall back to the default subscription");
        }
    }

    [Fact]
    public void Hooks_RetryKeyVaultListing_IsBoundedRetriesOnlyAuthErrors_AndAbortsWhenExhausted()
    {
        // The role assignment made in the same provision needs time to propagate. Code only, no comments.
        var sh = CodeOnly("infra/hooks/postprovision.sh");
        sh.ShouldContain("seq 1 30");
        sh.ShouldContain("grep -qiE 'Forbidden|AuthorizationFailed");
        Regex.IsMatch(sh, @"\$listed"" != true \]\]; then(?:(?!\bfi\b).)*?exit 1", RegexOptions.Singleline)
            .ShouldBeTrue("postprovision.sh must abort once the retries are exhausted");

        var ps1 = CodeOnly("infra/hooks/postprovision.ps1");
        ps1.ShouldContain("$attempt -le 30");
        Regex.IsMatch(ps1, @"notmatch 'Forbidden\|AuthorizationFailed[^']*'\)\s*\{\s*throw").ShouldBeTrue("non-auth failures must abort");
        Regex.IsMatch(ps1, @"\$attempt -eq 30\)\s*\{\s*throw").ShouldBeTrue("exhausted retries must abort");
    }

    [RequiresToolFact("bash")]
    public void PostprovisionSh_RetriesAuthorizationErrors_ThenSucceeds()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(forbiddenListings: 2, listNames: "nachos-bootstrap-admin-key");

        result.ExitCode.ShouldBe(0, result.StdErr);
        toolbox.Count("keyvault secret list").ShouldBe(3);
        toolbox.Count("firewall-rule delete").ShouldBe(1, "the temporary firewall rule is always removed");
    }

    [RequiresToolFact("bash")]
    public void PostprovisionSh_GivesUpAfterThirtyAuthorizationErrors()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(forbiddenListings: 40);

        result.ExitCode.ShouldNotBe(0);
        toolbox.Count("keyvault secret list").ShouldBe(30);
        toolbox.Count("secret set").ShouldBe(0);
        toolbox.Count("firewall-rule delete").ShouldBe(1);
    }

    [RequiresToolFact("bash")]
    public void PostprovisionSh_DoesNotRetryOtherListFailures()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(listError: "other");

        result.ExitCode.ShouldNotBe(0);
        toolbox.Count("keyvault secret list").ShouldBe(1);
        toolbox.Count("secret set").ShouldBe(0, "a failed listing must never be read as 'secret absent'");
    }

    [RequiresToolFact("bash")]
    public void PostprovisionSh_StoresAWellFormedAdminKey_WithoutEchoingIt()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(keyOutput: "aaa.bbb.ccc\n", listNames: "nachos-signing-key-0");

        result.ExitCode.ShouldBe(0, result.StdErr);
        toolbox.Count("secret set").ShouldBe(1);
        toolbox.Count("--name nachos-bootstrap-admin-key").ShouldBe(1);
        (result.StdOut + result.StdErr).ShouldNotContain("aaa.bbb.ccc");
        toolbox.Calls.Where(c => c.StartsWith("dotnet run", StringComparison.Ordinal))
            .ShouldAllBe(c => c.Contains("--no-launch-profile", StringComparison.Ordinal));
    }

    [RequiresToolFact("bash")]
    public void PostprovisionSh_RejectsMalformedAdminKeys_WithoutStoringAnything()
    {
        foreach (var output in new[] { "", "Using launch settings from x.json...\naaa.bbb.ccc\n", "not a jwt\n", "aaa.bbb\n" })
        {
            using var toolbox = new FakeToolbox();

            var result = toolbox.RunPostprovision(keyOutput: output, listNames: "nachos-signing-key-0");

            result.ExitCode.ShouldNotBe(0, $"output '{output}' must be rejected");
            toolbox.Count("--name nachos-bootstrap-admin-key").ShouldBe(0, $"output '{output}' must not be stored");
            toolbox.Count("firewall-rule delete").ShouldBe(1);
        }
    }

    [Fact]
    public void PostprovisionPs1_RequiresPowerShell74()
    {
        // $PSNativeCommandUseErrorActionPreference needs 7.4; without it a failed `secret list` would read as "absent".
        Regex.IsMatch(ReadHook("infra/hooks/postprovision.ps1"), @"^#Requires -Version 7\.4\s*$", RegexOptions.Multiline)
            .ShouldBeTrue();
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

    [Theory]
    [InlineData("on:\n  'push':\n  workflow_dispatch:\n")]
    [InlineData("on:\n  \"push\":\n  workflow_dispatch:\n")]
    [InlineData("on:\n  - \"push\"\n  - workflow_dispatch\n")]
    [InlineData("on: {push: {}, workflow_dispatch: {}}\n")]
    [InlineData("on: [push, workflow_dispatch]\n")]
    [InlineData("on:\n  ? push\n  workflow_dispatch:\n")]
    public void Scanner_FailsClosed_OnAnyNonDispatchOnlyTrigger(string triggers)
    {
        var workflow = "name: x\n" + triggers + "jobs:\n  j:\n    environment: azure-live\n    steps:\n      - run: azd up\n";

        PlantedWorkflowHits(workflow).ShouldNotBeEmpty($"triggers not exactly workflow_dispatch must not be exempt: {triggers}");
    }

    [Fact]
    public void Scanner_Exempts_QuotedAndFlowMappingDispatchOnly()
    {
        const string Job = "jobs:\n  j:\n    environment: azure-live\n    steps:\n      - run: azd up\n";
        PlantedWorkflowHits("on: {workflow_dispatch: {}}\n" + Job).ShouldBeEmpty();
        PlantedWorkflowHits("on:\n  'workflow_dispatch':\n" + Job).ShouldBeEmpty();
    }

    [Fact]
    public void Scanner_ExemptsPerJob_NotPerWorkflow()
    {
        const string Workflow =
            "name: x\non: workflow_dispatch\njobs:\n" +
            "  gate:\n    runs-on: ubuntu-latest\n    environment: azure-live\n    steps:\n      - run: echo ok\n" +
            "  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: azd up\n";

        var hits = PlantedWorkflowHits(Workflow);

        hits.ShouldHaveSingleItem().Line.ShouldBe(12);
    }

    [Fact]
    public void Scanner_StepInput_NamedEnvironment_DoesNotGrantTheExemption()
    {
        const string Workflow =
            "on: workflow_dispatch\njobs:\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n" +
            "      - uses: some/action@v1\n        with:\n          environment: azure-live\n      - run: azd up\n";

        PlantedWorkflowHits(Workflow).ShouldNotBeEmpty();
    }

    [Fact]
    public void Scanner_FailsClosed_WhenJobsCannotBeParsed()
    {
        PlantedWorkflowHits("on: workflow_dispatch\nsteps:\n  - run: azd up\n").ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("      - uses: azure/login@v2\n")]
    [InlineData("      - uses: azure/arm-deploy@v2\n")]
    [InlineData("      - uses: Azure/container-apps-deploy-action@v2\n")]
    [InlineData("      - uses: azure/sql-action@v2\n")]
    [InlineData("      - uses: \"azure/webapps-deploy@v3\"\n")]
    [InlineData("      - uses: docker/build-push-action@v6\n        with:\n          push: true\n")]
    [InlineData("      - run: docker image push example.azurecr.io/x:1\n")]
    [InlineData("      - run: docker push example.azurecr.io/x:1\n")]
    public void Scanner_FlagsAzureActionsAndImagePushes(string step)
    {
        var workflow = "on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n" + step;

        PlantedWorkflowHits(workflow).ShouldNotBeEmpty(step);
    }

    [Fact]
    public void Scanner_ScansCompositeActions_AndNeverExemptsThem()
    {
        PlantedFileHits(".github/actions/deploy/action.yml", "runs:\n  using: composite\n  steps:\n    - run: azd up\n    - uses: azure/login@v2\n")
            .Count.ShouldBe(2);
        PlantedFileHits(".github/actions/deploy/script.sh", "az group create -n x -l y\n").ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("      - run: az login --service-principal -u x -p y --tenant z\n")]
    [InlineData("      - run: az containerapp update -n api --image example.azurecr.io/x:1\n")]
    [InlineData("      - run: az sql server list\n")]
    [InlineData("      - run: az keyvault secret show --name x\n")]
    [InlineData("      - run: az webapp deploy --name x\n")]
    [InlineData("      - run: az group create -n x -l y\n")]
    [InlineData("      - run: docker buildx build --push -t example.azurecr.io/x:1 .\n")]
    [InlineData("      - run: docker build --push -t example.azurecr.io/x:1 .\n")]
    [InlineData("      - run: docker buildx build --output type=registry,ref=example.azurecr.io/x:1 .\n")]
    public void Scanner_FlagsAzCommandsAndPushingBuilds(string step)
    {
        PlantedWorkflowHits("on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n" + step).ShouldNotBeEmpty(step);
    }

    [Theory]
    [InlineData("org/repo/.github/workflows/deploy.yml@main")]
    [InlineData("'Org/Repo/.github/workflows/deploy.yml@v1'")]
    public void Scanner_FlagsReusableWorkflowsFromOtherRepositories(string uses)
    {
        PlantedWorkflowHits($"on: push\njobs:\n  j:\n    uses: {uses}\n    secrets: inherit\n").ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("      - run: az bicep build --file infra/main.bicep --stdout\n")]
    [InlineData("      - run: az bicep lint --file infra/main.bicep\n")]
    [InlineData("      - run: docker build -t example:ci .\n")]
    [InlineData("      - run: azd package\n")]
    public void Scanner_AllowsOfflineChecks(string step)
    {
        // Spec 18.3: offline `az bicep` checks, local image builds, and `azd package` need no consent.
        PlantedWorkflowHits("on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n" + step).ShouldBeEmpty(step);
    }

    [Fact]
    public void Scanner_AllowsLocalReusableWorkflows()
    {
        PlantedWorkflowHits("on: push\njobs:\n  j:\n    uses: ./.github/workflows/build.yml\n").ShouldBeEmpty();
    }

    [Theory]
    // The azure-live line sits inside a multi-line quoted scalar.
    [InlineData("on: workflow_dispatch\njobs:\n  j:\n    name: \"Deploy\n    environment: azure-live\n    \"\n    steps:\n      - run: azd up\n")]
    // A multi-line quoted scalar hides a complex key that redefines the trigger.
    [InlineData("name: \"x\non: workflow_dispatch\n\"\n? on\n: push\njobs:\n  j:\n    environment: azure-live\n    steps:\n      - run: azd up\n")]
    // Complex key on its own.
    [InlineData("on: workflow_dispatch\n? on\n: push\njobs:\n  j:\n    environment: azure-live\n    steps:\n      - run: azd up\n")]
    // Duplicate top-level on: key (YAML parsers differ on which wins).
    [InlineData("on: workflow_dispatch\non: push\njobs:\n  j:\n    environment: azure-live\n    steps:\n      - run: azd up\n")]
    public void Scanner_FailsClosed_OnYamlThatCannotBeReadSafely(string workflow)
    {
        PlantedWorkflowHits(workflow).ShouldNotBeEmpty();
    }

    [Fact]
    public void Scanner_StillExempts_BalancedQuotesAndApostropheInsideQuotes()
    {
        const string Workflow =
            "on: workflow_dispatch\njobs:\n  j:\n    environment: azure-live\n    steps:\n" +
            "      - run: echo \"it's fine\"\n      - run: azd up\n";

        PlantedWorkflowHits(Workflow).ShouldBeEmpty();
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>Hook text with comment-only lines removed.</summary>
    private static string CodeOnly(string relativePath) =>
        string.Join('\n', ReadHook(relativePath).Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    private static string ReadHook(string relativePath) => File.ReadAllText(RepoPaths.Combine(relativePath));

    /// <summary>Hook lines with shell (<c>\</c>) and PowerShell (<c>`</c>) line continuations joined.</summary>
    private static string[] LogicalLines(string relativePath) =>
        Regex.Replace(ReadHook(relativePath), @"(\\|`)[ \t]*\r?\n[ \t]*", " ").Split('\n');

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

    /// <summary>Reads a literal number, or the literal inside <c>[json('0.5')]</c>.</summary>
    private static double NumberOrJsonLiteral(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        var match = Regex.Match(value.GetString()!, @"^\[json\('([\d.]+)'\)\]$");
        match.Success.ShouldBeTrue($"unexpected numeric expression '{value}'");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Resolves a literal int, or a <c>[variables('name')]</c> reference to an int variable.</summary>
    private static int ResolveInt(JsonDocument template, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt32();
        }

        var match = Regex.Match(value.GetString()!, @"^\[variables\('(\w+)'\)\]$");
        match.Success.ShouldBeTrue($"unexpected expression '{value}'");
        var name = match.Groups[1].Value;
        return Descendants(template.RootElement)
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("variables", out _))
            .Select(e => e.GetProperty("variables"))
            .First(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number)
            .GetProperty(name).GetInt32();
    }

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

    private static string SampleValue(string parameter, string apiExists) => parameter switch
    {
        "apiExists" => apiExists,
        "environmentName" => "'nachos-test'",
        "location" => "'australiaeast'",
        "principalId" => "'00000000-0000-0000-0000-000000000001'",
        "principalLogin" => "'dev@example.com'",
        "openAiEndpoint" => "''",
        "principalType" => "'User'",
        _ => throw new InvalidOperationException($"Add a sample value for parameter '{parameter}' to SampleValue()."),
    };
}
