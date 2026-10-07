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

    /// <summary>
    /// mendhak/http-https-echo (MIT), OCI index of tag 42: answers 200 on every path on 8080. To replace it, change
    /// the default in main.bicep and main.parameters.json and re-run <see cref="Placeholder_Answers_ProbePaths"/>.
    /// </summary>
    /// <summary>postdeploy's polling budget (attempts, 6 s apart), the same in both scripts.</summary>
    internal const int PostdeployAttempts = 10;

    /// <summary>What the placeholder echo server answers on /health/ready (abridged).</summary>
    internal const string EchoBody = "{\"path\":\"/health/ready\",\"headers\":{\"host\":\"nachos-api\"},\"method\":\"GET\"}";

    internal const string PlaceholderStillServing =
        "The placeholder image is still serving (or the API is not healthy). Re-run `azd deploy`; if that cannot succeed, run `azd down`.";

    private const string PlaceholderImage =
        "ghcr.io/mendhak/http-https-echo@sha256:a265f55c86cb3baead76fdf379dc8e9e6440ed121874e101e60b833e05bce8d4";

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
        var diagnostics = BicepDiagnostics.Find(result.StdErr);
        diagnostics.ShouldBeEmpty(diagnostics.Count == 0 ? null : BicepCli.Explain(diagnostics, BicepCli.Version()));
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
                var diagnostics = BicepDiagnostics.Find(result.StdErr);
                diagnostics.ShouldBeEmpty(diagnostics.Count == 0 ? null : BicepCli.Explain(diagnostics, BicepCli.Version()));
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

        // Always-on HTTP probes, as a literal array: `azd deploy` copies the live app and swaps only the image,
        // so the first real revision inherits exactly the probes the placeholder revision had (C3).
        var probes = ApiContainer(template).GetProperty("probes");
        probes.ValueKind.ShouldBe(JsonValueKind.Array, "probes must be a literal array, not a conditional expression");
        var byType = probes.EnumerateArray().ToDictionary(p => p.GetProperty("type").GetString()!, StringComparer.Ordinal);
        byType.Keys.Order(StringComparer.Ordinal).ShouldBe(["Liveness", "Readiness"]);
        foreach (var (type, path) in new[] { ("Liveness", "/health/live"), ("Readiness", "/health/ready") })
        {
            var probe = byType[type];
            probe.TryGetProperty("tcpSocket", out _).ShouldBeFalse($"{type} must be an HTTP probe");
            var httpGet = probe.GetProperty("httpGet");
            httpGet.GetProperty("path").GetString().ShouldBe(path);
            ResolveInt(template, httpGet.GetProperty("port")).ShouldBe(8080);
        }
    }

    [Theory]
    [InlineData(PlaceholderImage, true)]
    [InlineData("x:42", false)]
    [InlineData("x:42@sha256:a265f55c86cb3baead76fdf379dc8e9e6440ed121874e101e60b833e05bce8d4", false)]
    [InlineData("x:latest", false)]
    [InlineData("x@sha256:a265f55c86cb3baead76fdf379dc8e9e6440ed121874e101e60b833e05bce8d", false)]
    [InlineData("ghcr.io/mendhak/http-https-echo", false)]
    [InlineData("latest@sha256:a265f55c86cb3baead76fdf379dc8e9e6440ed121874e101e60b833e05bce8d4", false)]
    public void DigestPin_AcceptsOnlyTaglessDigestReferences(string reference, bool pinned)
    {
        IsDigestPinned(reference).ShouldBe(pinned, reference);
    }

    [RequiresToolFact("az", "bicep")]
    public void Container_App_Image_IsDigestPinnedPlaceholder_AndKeepsDeployedImageWhenItExists()
    {
        using var template = ArmTemplate();

        // A third-party image runs with the managed identity until the first deploy, so it is pinned by digest.
        var placeholder = template.RootElement.GetProperty("parameters").GetProperty("placeholderImage")
            .GetProperty("defaultValue").GetString()!;
        placeholder.ShouldBe(PlaceholderImage);
        IsDigestPinned(placeholder).ShouldBeTrue();
        var apiDeployment = Descendants(template.RootElement).Single(e =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("type", out var t) && t.GetString() == "Microsoft.Resources/deployments"
            && e.TryGetProperty("name", out var n) && n.GetString() == "api-app");
        apiDeployment.GetProperty("properties").GetProperty("parameters").GetProperty("containerImage").GetProperty("value")
            .GetString().ShouldBe("[parameters('placeholderImage')]");

        var container = ApiContainer(template);
        var image = container.GetProperty("image").GetString()!;
        image.ShouldStartWith("[if(parameters('apiExists'),");
        image.ShouldEndWith(", parameters('containerImage'))]");
        // The placeholder needs no arguments; nothing placeholder-specific may leak into the real revision.
        container.TryGetProperty("args", out _).ShouldBeFalse();
        container.TryGetProperty("command", out _).ShouldBeFalse();

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
        values.GetProperty("placeholderImage").GetProperty("value").GetString().ShouldBe("${NACHOS_PLACEHOLDER_IMAGE=" + PlaceholderImage + "}");
    }

    [RequiresDockerDaemonFact]
    public async Task Placeholder_Answers_ProbePaths()
    {
        // Image, paths and port come from the compiled template, so this checks what would really be deployed.
        using var template = ArmTemplate();
        var image = template.RootElement.GetProperty("parameters").GetProperty("placeholderImage").GetProperty("defaultValue").GetString()!;
        var probes = ApiContainer(template).GetProperty("probes").EnumerateArray()
            .Select(p => p.GetProperty("httpGet"))
            .Select(h => (Path: h.GetProperty("path").GetString()!, Port: ResolveInt(template, h.GetProperty("port"))))
            .ToList();
        probes.ShouldNotBeEmpty();
        var port = probes.Select(p => p.Port).Distinct().ShouldHaveSingleItem();

        var docker = Tools.Require("docker");
        var run = Tools.Run(docker, ["run", "-d", "--rm", "-p", $"127.0.0.1::{port}", image]);
        run.ExitCode.ShouldBe(0, $"docker run {image} failed (a pull failure fails this test): {run.StdErr}");
        var container = run.StdOut.Trim();
        try
        {
            var mapping = Tools.Run(docker, ["port", container, $"{port}/tcp"]);
            mapping.ExitCode.ShouldBe(0, mapping.StdErr);
            var hostPort = mapping.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim().Split(':')[^1];

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            foreach (var (path, _) in probes)
            {
                var uri = new Uri($"http://127.0.0.1:{hostPort}{path}");
                string? last = null;
                while (true)
                {
                    try
                    {
                        using var response = await http.GetAsync(uri);
                        var status = (int)response.StatusCode;
                        if (status is >= 200 and < 400)
                        {
                            break;
                        }

                        last = $"HTTP {status}";
                    }
                    catch (HttpRequestException ex)
                    {
                        last = ex.Message;
                    }
                    catch (TaskCanceledException ex)
                    {
                        last = ex.Message;
                    }

                    DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, $"{image} never answered {path} with 2xx/3xx; last: {last}");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        }
        finally
        {
            Tools.Run(docker, ["rm", "-f", container]);
        }
    }

    [RequiresToolFact("az", "bicep")]
    public void Container_App_Env_CarriesTheAuthContract_AndPreservesTheKeyRing()
    {
        using var template = ArmTemplate();
        var apiTemplate = ApiModuleTemplate(template);
        var variables = apiTemplate.GetProperty("variables");

        // Bicep owns every env var except the ring namespace, which is rotation state owned by the running app.
        variables.GetProperty("ringPrefix").GetString().ShouldBe("Nachos__Auth__NachosKey__Keys__");
        var baseEnv = variables.GetProperty("baseEnv").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString(), StringComparer.Ordinal);
        baseEnv["Nachos__Auth__Enabled"].ShouldBe("true");
        baseEnv.Keys.Where(k => k.StartsWith("Nachos__Auth__NachosKey__Keys__", StringComparison.Ordinal))
            .ShouldBeEmpty("baseEnv must not set ring entries; they come from the running app or the seed");

        // The WHOLE env expression is pinned: the ring F (existing entries with the prefix, in order, whole objects)
        // must be both the emptiness test and the else branch, and the then branch exactly the Kid 0 seed. Pinning
        // only substrings would let `empty(F) ? seed : seed` (ring reset on every provision) or
        // `: concat(F, seed)` (a duplicate slot 0) through.
        var env = ApiContainer(template).GetProperty("env").GetString()!;
        const string Head = "[concat(variables('baseEnv'), if(empty(";
        env.ShouldStartWith(Head);
        var ring = BalancedCall(env, Head.Length);
        ring.ShouldStartWith("filter(if(parameters('apiExists'), reference(");
        ring.ShouldEndWith(
            ".outputs.env.value, createArray()), lambda('e', startsWith(lambdaVariables('e').name, variables('ringPrefix'))))");
        const string Seed = "createArray(createObject('name', format('{0}0__Kid', variables('ringPrefix')), 'value', '0'))";
        env.ShouldBe($"{Head}{ring}), {Seed}, {ring}), variables('openAiEnv'))]");

        var fetchOutputs = Descendants(template.RootElement)
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("outputs", out var o) && o.ValueKind == JsonValueKind.Object
                && o.TryGetProperty("image", out _) && o.TryGetProperty("env", out _))
            .Select(e => e.GetProperty("outputs"))
            .ShouldHaveSingleItem();
        fetchOutputs.GetProperty("env").GetProperty("value").GetString()!
            .ShouldMatch(@"^\[coalesce\(tryGet\(reference\(.*\)\.template\.containers\[0\], 'env'\), createArray\(\)\)\]$");
    }

    [RequiresToolFact("az", "bicep")]
    public void Sql_ReassertsEntraOnlyAuthentication_OnEveryProvision()
    {
        using var template = ArmTemplate();

        var child = Resources(template, "Microsoft.Sql/servers/azureADOnlyAuthentications").ShouldHaveSingleItem();
        child.GetProperty("name").GetString()!.ShouldEndWith("'Default')]");
        child.GetProperty("properties").GetProperty("azureADOnlyAuthentication").ValueKind.ShouldBe(JsonValueKind.True);
    }

    [Fact]
    public void BicepVersion_ExplainsUnknownApiVersions_OnOlderBicep()
    {
        string[] diagnostics = ["main.bicep(3,1) : Warning BCP081: Resource type \"Microsoft.App/containerApps@2026-01-01\" does not have types available."];

        BicepCli.Explain(diagnostics, new Version(0, 39, 0))
            .ShouldContain("Bicep 0.39.0 is older than the 0.48.1 type data these API versions need");
        BicepCli.Explain(diagnostics, BicepCli.MinimumVersion).ShouldNotContain("is older than");
    }

    [RequiresToolFact("az", "bicep")]
    public void Bicep_IsAtLeastMinimumVersion()
    {
        var version = BicepCli.Version();

        version.ShouldBeGreaterThanOrEqualTo(
            BicepCli.MinimumVersion,
            $"Bicep {version} is older than the {BicepCli.MinimumVersion} type data these API versions need; upgrade Bicep.");
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

    [RequiresPosixToolFact("bash")]
    public void PostprovisionSh_RetriesAuthorizationErrors_ThenSucceeds()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(forbiddenListings: 2, listNames: "nachos-bootstrap-admin-key");

        result.ExitCode.ShouldBe(0, result.StdErr);
        toolbox.Count("keyvault secret list").ShouldBe(3);
        toolbox.Count("firewall-rule delete").ShouldBe(1, "the temporary firewall rule is always removed");
    }

    [RequiresPosixToolFact("bash")]
    public void PostprovisionSh_GivesUpAfterThirtyAuthorizationErrors()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(forbiddenListings: 40);

        result.ExitCode.ShouldNotBe(0);
        toolbox.Count("keyvault secret list").ShouldBe(30);
        toolbox.Count("secret set").ShouldBe(0);
        toolbox.Count("firewall-rule delete").ShouldBe(1);
    }

    [RequiresPosixToolFact("bash")]
    public void PostprovisionSh_DoesNotRetryOtherListFailures()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(listError: "other");

        result.ExitCode.ShouldNotBe(0);
        toolbox.Count("keyvault secret list").ShouldBe(1);
        toolbox.Count("secret set").ShouldBe(0, "a failed listing must never be read as 'secret absent'");
    }

    [RequiresPosixToolFact("bash")]
    public void PostprovisionSh_StoresAWellFormedAdminKey_WithoutEchoingIt()
    {
        const string Key = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.c2lnbmF0dXJl";
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(keyOutput: Key + "\n", listNames: "nachos-signing-key-0");

        result.ExitCode.ShouldBe(0, result.StdErr);
        toolbox.Count("secret set").ShouldBe(1);
        toolbox.Count("--name nachos-bootstrap-admin-key").ShouldBe(1);
        // Exactly what the CLI minted (surrounding whitespace trimmed) reaches Key Vault, and nothing else.
        toolbox.StoredSecret("nachos-bootstrap-admin-key").ShouldBe(Key);
        var output = result.StdOut + result.StdErr;
        output.ShouldNotContain(Key);
        output.ShouldNotContain(FakeToolbox.SigningMaterial);
        toolbox.Calls.Where(c => c.StartsWith("dotnet run", StringComparison.Ordinal))
            .ShouldAllBe(c => c.Contains("--no-launch-profile", StringComparison.Ordinal));
        toolbox.Calls.Where(c => c.Contains("keys create", StringComparison.Ordinal)).ShouldHaveSingleItem()
            .ShouldContain("--admin --kid 0 --signing-secret-env NACHOS_SIGNING_SECRET");
    }

    [RequiresPosixToolFact("bash")]
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

    [Fact]
    public void AzureYaml_Hooks_FailClosed()
    {
        // A failed postprovision must stop `azd up` before `azd deploy` (the bootstrap contract depends on it).
        var yaml = File.ReadAllText(RepoPaths.Combine("azure.yaml")).Replace("\r", string.Empty, StringComparison.Ordinal);
        foreach (var hook in new[] { "postprovision", "postdeploy" })
        {
            var block = Regex.Match(yaml, $@"^  {hook}:\n((?:    .*\n|\n)*)", RegexOptions.Multiline);
            block.Success.ShouldBeTrue($"azure.yaml has no {hook} hook");
            var body = block.Groups[1].Value;
            foreach (var platform in new[] { "windows", "posix" })
            {
                var section = Regex.Match(body, $@"^    {platform}:\n((?:      .*\n)*)", RegexOptions.Multiline);
                section.Success.ShouldBeTrue($"{hook} has no {platform} section");
                Regex.IsMatch(section.Groups[1].Value, @"^\s*continueOnError:\s*false\s*$", RegexOptions.Multiline)
                    .ShouldBeTrue($"{hook}/{platform} must declare continueOnError: false");
            }
        }
    }

    [Fact]
    public void Hooks_MintAdminKeyWithKidZero()
    {
        // The signing-key contract: the bootstrap admin key is signed with ring entry Kid 0 (nachos-signing-key-0).
        foreach (var hook in PostprovisionHooks)
        {
            var mints = LogicalLines(hook).Where(l => l.Contains("keys create", StringComparison.Ordinal)).ToList();
            mints.ShouldNotBeEmpty($"{hook} never mints the admin key");
            mints.Where(l => !Regex.IsMatch(l, @"\s--kid\s+0(\s|$)")).ShouldBeEmpty($"{hook}: every keys create needs --kid 0");
        }
    }

    [RequiresPosixToolFact("bash")]
    public void PostprovisionSh_ContinuesWhenTheExistingUserHasTheExpectedSid()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(existingSid: FakeToolbox.ExpectedSid, listNames: "nachos-bootstrap-admin-key");

        result.ExitCode.ShouldBe(0, result.StdErr);
        toolbox.Count("keyvault secret list").ShouldBe(1);
    }

    [RequiresPosixToolFact("bash")]
    public void PostprovisionSh_AbortsWhenTheExistingUserHasAStaleSid()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovision(existingSid: "0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", listNames: "nachos-signing-key-0", keyOutput: "a.b.c\n");

        result.ExitCode.ShouldNotBe(0);
        result.StdErr.ShouldContain("id-test");
        result.StdErr.ShouldContain("recreated");
        toolbox.Count("-i ").ShouldBe(0, "no user/role script may run against a stale user");
        toolbox.Count("keyvault").ShouldBe(0, "nothing may be written to Key Vault");
        toolbox.Count("firewall-rule delete").ShouldBe(1);
    }

    [Fact]
    public void PostprovisionPs1_ComparesTheExistingUsersSid_BeforeCreatingIt()
    {
        var ps1 = CodeOnly("infra/hooks/postprovision.ps1");

        var lookup = ps1.IndexOf("CONVERT(varchar(34), sid, 1) FROM sys.database_principals", StringComparison.Ordinal);
        lookup.ShouldBeGreaterThanOrEqualTo(0, "postprovision.ps1 must read the existing user's SID");
        var mismatch = Regex.Match(ps1, @"-cne \$sid\)\s*\{\s*throw ""[^""]*recreated", RegexOptions.Singleline);
        mismatch.Success.ShouldBeTrue("a stale SID must throw, naming the recreated identity");
        mismatch.Index.ShouldBeLessThan(ps1.IndexOf("CREATE USER", StringComparison.Ordinal));
        mismatch.Index.ShouldBeLessThan(ps1.IndexOf("keyvault", StringComparison.Ordinal));
    }

    [RequiresPosixToolFact("bash")]
    public void PostdeploySh_AcceptsTheRealApisHealthyBody_AtOnce()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostdeploy(["Healthy\n"]);

        result.ExitCode.ShouldBe(0, result.StdErr);
        var gets = toolbox.Calls.Where(c => c.StartsWith("curl", StringComparison.Ordinal)).ToList();
        gets.ShouldHaveSingleItem().ShouldContain("--max-time 30");
        toolbox.Count("sleep").ShouldBe(0);
    }

    [RequiresPosixToolFact("bash")]
    public void PostdeploySh_KeepsPolling_WhileTrafficStillReachesThePlaceholder()
    {
        // azd waits for the ARM operation, not for the traffic switch: the first GET can still hit the placeholder.
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostdeploy([EchoBody, "Degraded", "Healthy"]);

        result.ExitCode.ShouldBe(0, result.StdErr);
        toolbox.Count("curl ").ShouldBe(3);
        toolbox.Count("sleep 6").ShouldBe(2);
    }

    [RequiresPosixToolFact("bash")]
    public void PostdeploySh_FailsAfterTheWholeBudget_WhenTheBodyIsNeverExactlyHealthy()
    {
        foreach (var body in new[] { EchoBody, "Degraded", "Unhealthy", "{\"status\":\"Healthy\"}", "Healthy, mostly", "" })
        {
            using var toolbox = new FakeToolbox();

            var result = toolbox.RunPostdeploy([body]);

            result.ExitCode.ShouldNotBe(0, $"body '{body}' must be rejected");
            result.StdErr.ShouldContain(PlaceholderStillServing);
            toolbox.Count("curl ").ShouldBe(PostdeployAttempts, $"body '{body}' is retried for the whole budget");
            toolbox.Count("sleep 6").ShouldBe(PostdeployAttempts - 1);
        }
    }

    [RequiresPosixToolFact("bash")]
    public void PostdeploySh_FailsAfterTheWholeBudget_WhenEveryRequestFails()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostdeploy(["Healthy"], curlFails: true);

        result.ExitCode.ShouldNotBe(0, "a request that never succeeds must not pass");
        result.StdErr.ShouldContain(PlaceholderStillServing);
        toolbox.Count("curl ").ShouldBe(PostdeployAttempts);
    }

    [Fact]
    public void Postdeploy_Scripts_RequireExactlyHealthy_AndDocumentTheFailSafe()
    {
        const string FailSafe =
            "If postprovision succeeded but `azd deploy` failed or was skipped, the public placeholder stays live " +
            "(it echoes request headers); redeploy with `azd deploy`, or `azd down` if that is not possible.";
        foreach (var hook in new[] { "infra/hooks/postdeploy.sh", "infra/hooks/postdeploy.ps1" })
        {
            var text = ReadHook(hook);
            Regex.Replace(text, @"\s*\n#\s*", " ").ShouldContain(FailSafe, customMessage: $"{hook} must document the fail-safe");
            text.ShouldContain(PlaceholderStillServing, customMessage: hook);
        }

        CodeOnly("infra/hooks/postdeploy.sh").ShouldContain("--max-time 30");
        CodeOnly("infra/hooks/postdeploy.ps1").ShouldContain("-TimeoutSec 30");
    }

    [Fact]
    public void ServiceDefaults_HealthEndpoints_KeepThePlainTextWriter()
    {
        // Cross-task guard: postdeploy recognises the real API by the default writer's plain-text "Healthy" body.
        var extensions = File.ReadAllText(RepoPaths.Combine("src/Nachos.ServiceDefaults/Extensions.cs"));

        Regex.IsMatch(extensions, @"\bResponseWriter\s*=").ShouldBeFalse(
            "postdeploy.* asserts the plain-text 'Healthy' body; update them together");
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

    [Fact]
    public void Scanner_FlagsAMultiLineBuildxPush_AtItsFirstPhysicalLine()
    {
        // The registry is not ACR on purpose, so only the joined `docker buildx build \ --push` statement can hit
        // line 7; the standalone --push flag would only hit line 8.
        const string Workflow =
            "on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n      - run: |\n" +
            "          docker buildx build \\\n            --push \\\n            -t ghcr.io/x/api:${{ github.sha }} .\n";

        PlantedWorkflowHits(Workflow).ShouldContain(h => h.Line == 7);
    }

    [Theory]
    // The reviewer's ACR example: login action, ACR reference and a multi-line buildx --push.
    [InlineData(
        "      - uses: docker/login-action@v3\n" +
        "        with: { registry: nachos.azurecr.io, username: ${{ secrets.ACR_USER }}, password: ${{ secrets.ACR_PASS }} }\n" +
        "      - run: |\n          docker buildx build \\\n            --push \\\n            -t nachos.azurecr.io/api:${{ github.sha }} .\n")]
    // type=registry on a backslash continuation line.
    [InlineData("      - run: |\n          docker buildx build \\\n            --output type=registry,ref=ghcr.io/x/api:1 .\n")]
    // PowerShell backtick continuation.
    [InlineData("      - shell: pwsh\n        run: |\n          docker buildx build `\n            --push `\n            -t ghcr.io/x/api:1 .\n")]
    // Backtick continuations that ONLY joining can see (no single physical line matches any pattern).
    [InlineData("      - shell: pwsh\n        run: |\n          docker `\n            push ghcr.io/x/api:1\n")]
    [InlineData("      - shell: pwsh\n        run: |\n          az `\n            login --identity\n")]
    // More registry-push forms.
    [InlineData("      - run: docker buildx build -o type=image,name=ghcr.io/x/api:1,push=true .\n")]
    [InlineData("      - run: docker manifest push ghcr.io/x/api:1\n")]
    [InlineData("      - run: docker compose push\n")]
    [InlineData("      - run: docker buildx imagetools create -t ghcr.io/x/api:latest ghcr.io/x/api:1\n")]
    // A push split right after `docker`: only joining the continuation can see it.
    [InlineData("      - run: |\n          docker \\\n            push ghcr.io/x/api:1\n")]
    [InlineData("      - uses: docker/login-action@v3\n        with:\n          registry: ghcr.io\n")]
    [InlineData("      - uses: 'docker/login-action@v3'\n")]
    [InlineData("      - run: docker pull nachos.azurecr.io/api:1\n")]
    [InlineData("      - uses: docker://nachos.AzureCR.io/build-tool:1\n")]
    public void Scanner_FlagsRegistryPushes_LoginsAndAcrReferences(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldNotBeEmpty(step);
    }

    [Theory]
    [InlineData("      - run: docker build -t x .\n")]
    [InlineData("      # push the image in the release workflow\n      - run: docker build -t x .\n")]
    [InlineData("      - run: docker build -t x . # docker buildx build --push later\n")]
    [InlineData("      - run: git push --push-option=ci.skip origin HEAD\n")]
    [InlineData("      - run: docker compose up -d\n")]
    [InlineData("      - run: git push origin main\n")]
    [InlineData("      - run: npm publish\n")]
    // A trailing backtick in a step name is Markdown, not a PowerShell continuation into the next key.
    [InlineData("      - name: Install `azd`\n        run: curl -fsSL https://aka.ms/install-azd.sh | bash\n")]
    [InlineData("      - name: Lint with `az bicep`\n        run: bicep lint infra/main.bicep\n")]
    public void Scanner_DoesNotFlag_LocalBuildsOrPushInComments(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldBeEmpty(step);
    }

    [Theory]
    [InlineData("      - run: az bicep publish --file x.bicep --target br:registry.example.com/bicep/x:v1\n")]
    [InlineData("      - run: az bicep restore --file infra/main.bicep\n")]
    [InlineData("      - run: AZ BICEP PUBLISH --file x.bicep --target br:registry.example.com/x:v1\n")]
    [InlineData("      - run: az bicep build-foo --file x.bicep\n")]
    public void Scanner_FlagsAzBicepSubcommandsThatReachARegistry(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldNotBeEmpty(step);
    }

    [Theory]
    [InlineData("      - run: az bicep build-params --file infra/main.bicepparam\n")]
    [InlineData("      - run: az bicep format --file infra/main.bicep\n")]
    [InlineData("      - run: az bicep decompile --file main.json\n")]
    [InlineData("      - run: az bicep version\n")]
    [InlineData("      - run: az bicep install\n")]
    [InlineData("      - run: az bicep upgrade\n")]
    [InlineData("      - run: AZ BICEP BUILD --file infra/main.bicep\n")]
    public void Scanner_AllowsOfflineAzBicepSubcommands(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldBeEmpty(step);
    }

    [Theory]
    [InlineData("      - run: azd hooks run postprovision\n")]
    [InlineData("      - run: azd auth login --client-id x\n")]
    [InlineData("      - run: azd env refresh\n")]
    [InlineData("      - run: azd env new dev\n")]
    [InlineData("      - run: azd init -t x\n")]
    [InlineData("      - run: azd pipeline config\n")]
    [InlineData("      - run: AZD UP\n")]
    public void Scanner_FlagsAzdCommandsThatTouchAzure(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldNotBeEmpty(step);
    }

    [Theory]
    [InlineData("      - run: azd version\n")]
    [InlineData("      - run: azd config show\n")]
    [InlineData("      - run: azd package api --output-path out/api.tar\n")]
    public void Scanner_AllowsOfflineAzdCommands(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldBeEmpty(step);
    }

    [Theory]
    [InlineData("      - run: echo hi # az login later\n")]
    [InlineData("      - name: build # az group\n        run: echo hi\n")]
    [InlineData("      # azd up is run by hand from deploy.yml\n      - run: echo hi\n")]
    public void Scanner_IgnoresCommentOnlyMentions(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldBeEmpty(step);
    }

    [Theory]
    [InlineData("      - run: az login --identity # sign in first\n")]
    [InlineData("      - run: echo \"#\" ; az group list\n")]
    // A multi-line shell string: bash closes it on the second line and then runs `az login`, so this file's
    // comments cannot be stripped safely.
    [InlineData("      - run: |\n          echo \"x\n          # \" ; az login\n")]
    public void Scanner_StillFlagsCommands_WithTrailingCommentsOrQuotedHashes(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldNotBeEmpty(step);
    }

    [Theory]
    // Multi-line flow collections: YAML reads environment as part of labels/tags, so the job has none.
    [InlineData("    labels: { a: 1,\n    environment: azure-live\n    }\n")]
    [InlineData("    tags: [a,\n    environment: azure-live\n    ]\n")]
    // Duplicate environment keys: parsers disagree on which wins (or reject the file).
    [InlineData("    environment: azure-live\n    environment: production\n")]
    [InlineData("    environment: azure-live\n    \"environment\": production\n")]
    // azure-live LAST, so only the duplicate-key rule (not "the last one wins") keeps these non-exempt.
    [InlineData("    environment: production\n    environment: azure-live\n")]
    [InlineData("    \"environment\": production\n    environment:\n      name: azure-live\n")]
    public void Scanner_FailsClosed_OnAmbiguousJobEnvironment(string jobKeys)
    {
        var workflow = "on: workflow_dispatch\njobs:\n  j:\n    runs-on: ubuntu-latest\n" + jobKeys + "    steps:\n      - run: azd up\n";

        PlantedWorkflowHits(workflow).ShouldNotBeEmpty(jobKeys);
    }

    [Fact]
    public void Scanner_JoinedStatement_IsExemptOnlyIfEveryPhysicalLineIs()
    {
        // `azd \` in a job without azure-live continues into the key line of the azure-live job; only the joined
        // statement matches, and it straddles an exempt and a non-exempt line, so it must be reported.
        const string Workflow =
            "on: workflow_dispatch\njobs:\n" +
            "  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: azd \\\n" +
            "  up:\n    runs-on: ubuntu-latest\n    environment: azure-live\n    steps:\n      - run: echo deploy\n";

        PlantedWorkflowHits(Workflow).ShouldHaveSingleItem().Line.ShouldBe(6);
    }

    [Fact]
    public void Scanner_JoinsBacktickContinuations_InScriptsOutsideYaml()
    {
        PlantedFileHits("eng/deploy.ps1", "docker `\n  push ghcr.io/x/api:1\n").ShouldHaveSingleItem().Line.ShouldBe(1);
    }

    [Theory]
    // Flags before the verb; an unknown flag before an allowed verb means the verb cannot be determined.
    [InlineData("azd --no-prompt up")]
    [InlineData("azd -e prod deploy")]
    [InlineData("azd --environment prod provision")]
    [InlineData("azd --cwd ./app --no-prompt down --force --purge")]
    [InlineData("azd --mystery-flag package")]
    // Executable names, quoting and paths.
    [InlineData("azd.exe up")]
    [InlineData("azd.cmd provision")]
    [InlineData("\"azd\" up")]
    [InlineData("'azd' up")]
    [InlineData("/usr/local/bin/azd up")]
    [InlineData(".\\azd.exe up")]
    [InlineData("& \"C:\\Program Files\\azd\\azd.exe\" up")]
    [InlineData("az.cmd login --identity")]
    [InlineData("az.exe group list")]
    [InlineData("\"az\" login")]
    [InlineData("az \"login\"")]
    [InlineData("az --debug login")]
    [InlineData("az --only-show-errors bicep publish --file x.bicep --target br:registry.example.com/x:v1")]
    [InlineData("/usr/bin/az account show")]
    // Hooks via backslash paths.
    [InlineData("pwsh .\\infra\\hooks\\postprovision.ps1")]
    [InlineData("bash infra\\hooks/postprovision.sh")]
    // Az PowerShell and raw ARM / Entra / storage endpoints.
    [InlineData("Connect-AzAccount -Identity")]
    [InlineData("New-AzResourceGroupDeployment -ResourceGroupName rg -TemplateFile infra/main.json")]
    [InlineData("New-AzDeployment -Location eastus -TemplateFile infra/main.json")]
    [InlineData("Set-AzContext -Subscription x")]
    [InlineData("Publish-AzWebApp -ResourceGroupName rg -Name x -ArchivePath a.zip")]
    [InlineData("Invoke-AzRestMethod -Path /subscriptions?api-version=2022-12-01")]
    [InlineData("curl -X PUT https://management.azure.com/subscriptions/x/resourcegroups/rg")]
    [InlineData("curl -d @body https://login.microsoftonline.com/tenant/oauth2/v2.0/token")]
    [InlineData("curl -T app.zip https://acct.blob.core.windows.net/releases/app.zip")]
    // Other registry pushers.
    [InlineData("podman push ghcr.io/x/api:1")]
    [InlineData("skopeo copy docker-archive:api.tar docker://ghcr.io/x/api:1")]
    [InlineData("oras push ghcr.io/x/artifact:1 file.txt")]
    [InlineData("crane push api.tar ghcr.io/x/api:1")]
    [InlineData("crane copy ghcr.io/x/api:1 ghcr.io/y/api:1")]
    [InlineData("buildah push api ghcr.io/x/api:1")]
    [InlineData("ctr images push ghcr.io/x/api:1")]
    [InlineData("ctr -n k8s.io images push ghcr.io/x/api:1")]
    [InlineData("dotnet publish src/Nachos.Api -t:PublishContainer")]
    [InlineData("dotnet publish src/Nachos.Api /t:PublishContainer")]
    [InlineData("dotnet publish src/Nachos.Api -p:ContainerRegistry=ghcr.io")]
    [InlineData("bicep publish infra/main.bicep --target br:registry.example.com/x:v1")]
    [InlineData("bicep restore infra/main.bicep")]
    public void Scanner_FlagsEveryFormOfAzureOrRegistryAccess(string command)
    {
        PlantedWorkflowHits(OnPush($"      - run: {command}\n")).ShouldNotBeEmpty(command);
    }

    [Theory]
    [InlineData("azd --no-prompt package api")]
    [InlineData("azd -e dev package")]
    [InlineData("azd --debug config show")]
    [InlineData("azd.exe version")]
    [InlineData("azd --version")]
    [InlineData("az --only-show-errors bicep build --file infra/main.bicep")]
    [InlineData("az.cmd bicep lint --file infra/main.bicep")]
    [InlineData("\"az\" bicep version")]
    [InlineData("bicep build infra/main.bicep --stdout")]
    [InlineData("bicep build-params infra/main.bicepparam")]
    [InlineData("bicep lint infra/main.bicep")]
    [InlineData("bicep format infra/main.bicep")]
    [InlineData("bicep decompile main.json")]
    [InlineData("bicep --version")]
    [InlineData("dotnet publish src/Nachos.Api -c Release")]
    [InlineData("curl -fsSL https://aka.ms/install-azd.sh | bash")]
    public void Scanner_AllowsOfflineFormsOfTheSameTools(string command)
    {
        PlantedWorkflowHits(OnPush($"      - run: {command}\n")).ShouldBeEmpty(command);
    }

    [Fact]
    public void Scanner_DoesNotFlagProseOrSchemaUrls()
    {
        PlantedWorkflowHits(OnPush("      - name: Lint bicep files\n        run: bicep lint infra/main.bicep\n")).ShouldBeEmpty();
        PlantedWorkflowHits(OnPush("      - name: Install azd\n        run: echo later\n")).ShouldBeEmpty();
        PlantedFileHits("eng/arm/params.json", "{ \"$schema\": \"https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#\" }\n")
            .ShouldBeEmpty();
        PlantedFileHits(".github/scripts/walk.mjs", "files.push(...await markdownFiles(path));\nfiles.push(path);\n").ShouldBeEmpty();
    }

    [Fact]
    public void Scanner_ScansGithubScripts_ButNotInstalledPackages()
    {
        PlantedFileHits(".github/scripts/deploy.sh", "azd up\n").ShouldHaveSingleItem().File.ShouldBe(".github/scripts/deploy.sh");
        PlantedFileHits(".github/scripts/node_modules/some-lib/index.js", "exec('az login')\n").ShouldBeEmpty();
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static string OnPush(string steps) => ScannerFixture.OnPush(steps);

    /// <summary>Hook text with comment-only lines removed.</summary>
    private static string CodeOnly(string relativePath) =>
        string.Join('\n', ReadHook(relativePath).Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    private static string ReadHook(string relativePath) => File.ReadAllText(RepoPaths.Combine(relativePath));

    /// <summary>Hook lines with shell (<c>\</c>) and PowerShell (<c>`</c>) line continuations joined.</summary>
    private static string[] LogicalLines(string relativePath) =>
        Regex.Replace(ReadHook(relativePath), @"(\\|`)[ \t]*\r?\n[ \t]*", " ").Split('\n');

    private static IReadOnlyList<ScanHit> PlantedWorkflowHits(string workflow) => ScannerFixture.PlantedWorkflowHits(workflow);

    private static IReadOnlyList<ScanHit> PlantedFileHits(string relativePath, string content) =>
        ScannerFixture.PlantedFileHits(relativePath, content);

    /// <summary>The ARM function call starting at <paramref name="start"/> (name through its matching parenthesis).</summary>
    private static string BalancedCall(string expression, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '\'')
            {
                inString = !inString; // ARM escapes a quote as '', which toggles twice
            }
            else if (!inString && c == '(')
            {
                depth++;
            }
            else if (!inString && c == ')' && --depth == 0)
            {
                return expression[start..(i + 1)];
            }
        }

        throw new ShouldAssertException($"No complete call at {start} in {expression}");
    }

    private static bool IsDigestPinned(string reference) =>
        Regex.IsMatch(reference, @"^[a-z0-9][a-z0-9._/-]*@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)
        && !reference.Contains("latest", StringComparison.OrdinalIgnoreCase);

    private static JsonElement ApiContainer(JsonDocument template) =>
        Resources(template, "Microsoft.App/containerApps").Single()
            .GetProperty("properties").GetProperty("template").GetProperty("containers").EnumerateArray().Single();

    /// <summary>The nested template of the api-app module deployment.</summary>
    private static JsonElement ApiModuleTemplate(JsonDocument template) =>
        Descendants(template.RootElement).Single(e =>
                e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("type", out var t) && t.GetString() == "Microsoft.Resources/deployments"
                && e.TryGetProperty("name", out var n) && n.GetString() == "api-app")
            .GetProperty("properties").GetProperty("template");

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
        "placeholderImage" => $"'{PlaceholderImage}'",
        _ => throw new InvalidOperationException($"Add a sample value for parameter '{parameter}' to SampleValue()."),
    };
}
