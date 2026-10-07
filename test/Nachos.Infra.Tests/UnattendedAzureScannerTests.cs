using Shouldly;
using static Nachos.Infra.Tests.ScannerFixture;

namespace Nachos.Infra.Tests;

/// <summary>
/// Scanner forms added after the first wide guard: docker and standalone bicep through the verb tokeniser, every
/// Az PowerShell cmdlet, more Azure endpoints, launchers, folded YAML, fail-closed verbs, and per-root file rules.
/// </summary>
public sealed class UnattendedAzureScannerTests
{
    [Theory]
    // docker: global flags before the verb, and every verb that reaches a registry.
    [InlineData("docker --config ./cfg push ghcr.io/x/api:1")]
    [InlineData("docker -H tcp://builder:2375 push ghcr.io/x/api:1")]
    [InlineData("docker --context ci push ghcr.io/x/api:1")]
    [InlineData("docker -c ci --log-level debug push ghcr.io/x/api:1")]
    [InlineData("docker login ghcr.io -u x --password-stdin")]
    [InlineData("docker logout ghcr.io")]
    [InlineData("docker -D manifest push ghcr.io/x/api:1")]
    [InlineData("docker --tlsverify image push ghcr.io/x/api:1")]
    [InlineData("docker compose -f compose.yml push")]
    [InlineData("docker buildx imagetools create -t ghcr.io/x/api:latest ghcr.io/x/api:1")]
    [InlineData("docker trust sign ghcr.io/x/api:1")]
    [InlineData("docker.exe push ghcr.io/x/api:1")]
    [InlineData("\"docker\" push ghcr.io/x/api:1")]
    [InlineData("/usr/bin/docker push ghcr.io/x/api:1")]
    [InlineData("docker --mystery-flag build -t x .")]
    [InlineData("docker-compose push")]
    // Standalone bicep through the same tokeniser.
    [InlineData("\"bicep\" publish infra/main.bicep --target br:registry.example.com/x:v1")]
    [InlineData("bicep --verbose publish infra/main.bicep --target br:registry.example.com/x:v1")]
    [InlineData("bicep.exe restore infra/main.bicep")]
    // Any Az PowerShell cmdlet (and AzureRM), not just a list of verbs.
    [InlineData("Login-AzAccount -Identity")]
    [InlineData("Select-AzSubscription -SubscriptionId x")]
    [InlineData("Get-AzAccessToken -ResourceUrl https://x")]
    [InlineData("Import-AzContainerAppAuthConfig -Name x")]
    [InlineData("New-AzureRmResourceGroupDeployment -ResourceGroupName rg")]
    // More Azure endpoints (public, US Gov, China clouds).
    [InlineData("curl https://kv-x.vault.azure.net/secrets/x?api-version=7.4")]
    [InlineData("sqlcmd -S tcp:sql-x.database.windows.net -d nachos")]
    [InlineData("curl -T app.zip https://app-x.scm.azurewebsites.net/api/zipdeploy")]
    [InlineData("curl https://app-x.azurewebsites.net/health")]
    [InlineData("curl https://management.usgovcloudapi.net/subscriptions")]
    [InlineData("curl https://management.chinacloudapi.cn/subscriptions")]
    // Launchers: az/azd as the argument of another command is flagged whatever follows.
    [InlineData("echo up | xargs azd")]
    [InlineData("xargs -n 1 azd < verbs.txt")]
    [InlineData("nohup azd package &")]
    [InlineData("setsid azd deploy")]
    [InlineData("stdbuf -oL az bicep build --file infra/main.bicep")]
    [InlineData("watch -n 5 az account show")]
    [InlineData("parallel azd ::: up down")]
    [InlineData("python -m azure.cli login --identity")]
    [InlineData("python3 -m azure.cli group list")]
    // A verb that cannot be determined is flagged (fail closed); only a bare mention or --version/--help is not.
    [InlineData("azd --no-prompt")]
    [InlineData("azd -e")]
    [InlineData("az --debug")]
    [InlineData("docker --debug")]
    public void FlagsTheForm(string command)
    {
        PlantedWorkflowHits(OnPush($"      - run: {command}\n")).ShouldNotBeEmpty(command);
    }

    [Theory]
    [InlineData("docker build -t x .")]
    [InlineData("docker buildx build -t x .")]
    [InlineData("docker run --rm x")]
    [InlineData("docker pull ghcr.io/x/api:1")]
    [InlineData("docker ps -a")]
    [InlineData("docker image ls")]
    [InlineData("docker compose up -d")]
    [InlineData("docker --version")]
    [InlineData("docker -H unix:///var/run/docker.sock build -t x .")]
    [InlineData("bicep build infra/main.bicep")]
    [InlineData("bicep build-params infra/main.bicepparam")]
    [InlineData("bicep lint infra/main.bicep")]
    [InlineData("bicep format infra/main.bicep")]
    [InlineData("bicep decompile main.json")]
    [InlineData("bicep version")]
    [InlineData("bicep install")]
    [InlineData("bicep upgrade")]
    [InlineData("pwsh -c 'Import-Module Az'")]
    [InlineData("pwsh -c 'Install-Module Az -Scope CurrentUser'")]
    [InlineData("azd --help")]
    public void AllowsTheOfflineForm(string command)
    {
        PlantedWorkflowHits(OnPush($"      - run: {command}\n")).ShouldBeEmpty(command);
    }

    [Theory]
    [InlineData(">")]
    [InlineData(">-")]
    [InlineData(">+")]
    public void JoinsFoldedRunBlocks_IntoOneStatement(string indicator)
    {
        // A folded scalar is one shell line: `azd` and `up` on separate lines still run `azd up`.
        var hits = PlantedWorkflowHits(OnPush($"      - run: {indicator}\n          azd\n          up\n"));

        hits.ShouldHaveSingleItem().Line.ShouldBe(7);
    }

    [Fact]
    public void DoesNotJoinLiteralRunBlocks()
    {
        // In a `|` block each line is its own command: `azd` alone prints help, `up` is another command.
        PlantedWorkflowHits(OnPush("      - run: |\n          azd\n          up\n")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(".github/actions/node_modules/action.yml", "runs:\n  using: composite\n  steps:\n    - run: azd up\n")]
    [InlineData("eng/node_modules/deploy.sh", "azd up\n")]
    [InlineData(".github/workflows/node_modules/x.yml", "on: push\njobs:\n  j:\n    steps:\n      - run: azd up\n")]
    public void ScansNodeModules_OutsideGithubScripts(string path, string content)
    {
        PlantedFileHits(path, content).ShouldNotBeEmpty(path);
    }

    [Theory]
    [InlineData(".github/scripts/walk.mjs", "const az = 1;\nfor (const az of list) {}\nconst s = \"az\" + x;\n")]
    [InlineData(".github/scripts/lib.cjs", "module.exports = { az: 1 };\nconst docs = 'see https://core.windows.net';\n")]
    [InlineData(".github/scripts/types.ts", "let az: number = 0;\naz = az + 1;\n")]
    [InlineData(".github/scripts/README.md", "Run `azd up` by hand; never `az login` in CI. Connect-AzAccount is not used.\n")]
    [InlineData(".github/scripts/fixtures/valid/deploy.sh", "azd up\naz login\n")]
    [InlineData(".github/scripts/package.json", "{ \"$schema\": \"https://schema.management.azure.com/x.json#\", \"name\": \"az\" }\n")]
    public void GithubScripts_JsAndDocs_AreNotReadAsShell(string path, string content)
    {
        PlantedFileHits(path, content).ShouldBeEmpty(path);
    }

    [Theory]
    [InlineData(".github/scripts/release.mjs", "execSync('azd up --no-prompt');\n")]
    [InlineData(".github/scripts/release.mjs", "execSync(\"docker push ghcr.io/x/api:1\");\n")]
    [InlineData(".github/scripts/release.js", "const image = 'nachos.azurecr.io/api:1';\n")]
    [InlineData(".github/scripts/release.ts", "await fetch('https://management.azure.com/subscriptions');\n")]
    [InlineData(".github/scripts/release.cjs", "spawn('pwsh', ['-c', 'Connect-AzAccount -Identity']);\n")]
    [InlineData(".github/scripts/package.json", "{ \"scripts\": { \"deploy\": \"azd deploy\" } }\n")]
    [InlineData(".github/scripts/deploy.sh", "az login --identity\n")]
    [InlineData(".github/scripts/deploy.ps1", "Connect-AzAccount -Identity\n")]
    public void GithubScripts_FlagUnambiguousCommands_InEveryFileType(string path, string content)
    {
        PlantedFileHits(path, content).ShouldNotBeEmpty(path);
    }
}
