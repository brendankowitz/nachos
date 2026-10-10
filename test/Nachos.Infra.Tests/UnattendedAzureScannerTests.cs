using Shouldly;
using static Nachos.Infra.Tests.ScannerFixture;

namespace Nachos.Infra.Tests;

/// <summary>
/// Scanner forms added after the first wide guard: docker and standalone bicep through the verb tokeniser, every
/// Az PowerShell cmdlet, more Azure endpoints, launchers, folded YAML, fail-closed verbs, per-root file rules, a
/// standalone bicep nested in az's arguments, multi-line plain/quoted YAML scalars, and Markdown run as a script.
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
    [InlineData("docker plugin push x")]
    [InlineData("docker-compose push")]
    // The other container tools through the tokeniser: global flags before push, unknown flags fail closed, launchers.
    [InlineData("docker-compose -f docker-compose.ci.yml push")]
    [InlineData("podman --storage-driver=vfs push")]
    [InlineData("nerdctl --namespace k8s.io push")]
    [InlineData("buildah --storage-driver vfs push")]
    [InlineData("podman --mystery build -t x .")]
    [InlineData("echo \"push x\" | xargs docker")]
    [InlineData("echo \"push x\" | xargs podman")]
    // Standalone bicep: allow-list, so unknown and experimental verbs are flagged like the registry ones.
    [InlineData("\"bicep\" publish infra/main.bicep --target br:registry.example.com/x:v1")]
    [InlineData("bicep --verbose publish infra/main.bicep --target br:registry.example.com/x:v1")]
    [InlineData("bicep.exe restore infra/main.bicep")]
    [InlineData("bicep restore main.bicep")]
    [InlineData("bicep teardown main.bicepparam")]
    [InlineData("bicep publish-extension index.json --target br:example.com/x:1")]
    [InlineData("bicep deploy main.bicepparam")]
    [InlineData("bicep test main.bicep")]
    [InlineData("bicep snapshot main.bicepparam")]
    [InlineData("bicep install")]
    [InlineData("bicep upgrade")]
    // The binary held in a variable or a command substitution: the verb is elsewhere, so the mention is flagged.
    [InlineData("\"$(command -v azd)\" up")]
    [InlineData("$(which azd) up")]
    [InlineData("`which azd` up")]
    [InlineData("AZD=$(command -v azd); \"$AZD\" up")]
    [InlineData("\"$(command -v az)\" login --identity")]
    [InlineData("\"$(command -v docker)\" push ghcr.io/x/api:1")]
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
    [InlineData("docker-compose -f docker-compose.ci.yml up -d")]
    [InlineData("docker-compose pull")]
    [InlineData("podman --storage-driver=vfs build -t x .")]
    [InlineData("nerdctl --namespace k8s.io images")]
    [InlineData("buildah --storage-driver vfs bud -t x .")]
    [InlineData("bicep build infra/main.bicep")]
    [InlineData("bicep build")]
    [InlineData("bicep build-params infra/main.bicepparam")]
    [InlineData("bicep build-params x.bicepparam")]
    [InlineData("bicep lint infra/main.bicep")]
    [InlineData("bicep lint")]
    [InlineData("bicep format infra/main.bicep")]
    [InlineData("bicep decompile main.json")]
    [InlineData("bicep decompile-params main.parameters.json")]
    [InlineData("bicep generate-params infra/main.bicep")]
    [InlineData("bicep version")]
    [InlineData("bicep --version")]
    [InlineData("pwsh -c 'Import-Module Az'")]
    [InlineData("pwsh -c 'Install-Module Az -Scope CurrentUser'")]
    [InlineData("azd --help")]
    // Prerequisite checks: the bare name is a mention, not a command with a verb.
    [InlineData("command -v azd")]
    [InlineData("which azd")]
    [InlineData("azd --version")]
    [InlineData("echo \"$(azd)\"")]
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

    [Theory]
    [InlineData("eng/x/README.md", "Covers offline Bicep validation, and SDK conformance/download producers.\n")]
    [InlineData("eng/x/README.md", "Install the `az` CLI, then run az login by hand.\n")]
    [InlineData("eng/x/NOTES.MD", "Run bicep teardown only after the az CLI is installed.\n")]
    public void Markdown_OutsideGithubScripts_IsProse_AmbiguousVerbsAreNotCommands(string path, string content)
    {
        PlantedFileHits(path, content).ShouldBeEmpty(path);
    }

    [Theory]
    [InlineData("eng/x/README.md", "```sh\nazd up\n```\n")]
    [InlineData("eng/x/README.md", "```sh\ndocker push ghcr.io/x/api:1\n```\n")]
    [InlineData("eng/x/README.md", "GET https://management.azure.com/subscriptions\n")]
    [InlineData("eng/x/README.md", "```pwsh\nConnect-AzAccount -Identity\n```\n")]
    [InlineData("eng/x/README.md", "image: nachos.azurecr.io/api:1\n")]
    [InlineData("eng/x/NOTES.MD", "azd deploy\n")]
    public void Markdown_OutsideGithubScripts_StillFlagsUnambiguousCommands(string path, string content)
    {
        PlantedFileHits(path, content).ShouldNotBeEmpty(path);
    }

    [Theory]
    [InlineData("eng/x/run.sh", "bicep teardown x\n")]
    [InlineData("eng/x/run.ps1", "bicep teardown x\n")]
    [InlineData("eng/x/run.yml", "on: push\njobs:\n  j:\n    steps:\n      - run: bicep teardown x\n")]
    [InlineData("eng/x/run.sh", "az login\n")]
    public void NonMarkdownFiles_UnderEng_KeepEveryRule(string path, string content)
    {
        PlantedFileHits(path, content).ShouldNotBeEmpty(path);
    }

    // ---- A1: a standalone bicep nested in an az command's arguments --------------------------------------------

    [Theory]
    [InlineData("az bicep build --file \"$(bicep publish infra/main.bicep --target br:registry.example.com/x:v1)\"")]
    [InlineData("az bicep build --file <(bicep teardown x)")]
    [InlineData("az bicep build --file (bicep teardown x)")]
    [InlineData("az --only-show-errors bicep build --file \"$(bicep restore main.bicep)\"")]
    [InlineData("bicep publish infra/main.bicep --target br:registry.example.com/x:v1")]
    public void FlagsStandaloneBicep_EvenInsideAnAzBicepCommand(string command)
    {
        PlantedWorkflowHits(OnPush($"      - run: {command}\n")).Select(h => h.Pattern).ShouldContain(CliInvocations.BicepRule, command);
    }

    [Theory]
    [InlineData("az bicep build --file x.bicep")]
    // `install` and `upgrade` are offline for `az bicep` but not on the standalone allow-list, so these are clean
    // only if az's own `bicep` verb (after any global flags) is never read as a standalone bicep.
    [InlineData("az bicep install")]
    [InlineData("az --only-show-errors bicep upgrade")]
    [InlineData("az -o json bicep install")]
    [InlineData("\"az\" \"bicep\" upgrade")]
    public void AzOwnBicepVerb_IsNotReadAsAStandaloneBicep(string command)
    {
        PlantedWorkflowHits(OnPush($"      - run: {command}\n")).ShouldBeEmpty(command);
    }

    [Fact]
    public void AzBicepPublish_IsFlaggedByTheAzRuleOnly()
    {
        PlantedWorkflowHits(OnPush("      - run: az bicep publish --file x.bicep --target br:registry.example.com/x:v1\n"))
            .Select(h => h.Pattern).ShouldBe([CliInvocations.AzRule]);
    }

    // ---- A2: multi-line plain and quoted YAML scalars ----------------------------------------------------------

    [Theory]
    [InlineData("      - run: azd\n          up\n")]
    [InlineData("      - run: \"azd\n          up\"\n")]
    [InlineData("      - run: 'azd\n          up'\n")]
    [InlineData("      - run: az\n          group\n          create -n x -l y\n")]
    // The value may start on the line after the key.
    [InlineData("      - run:\n          azd\n          up\n")]
    // with:/env: values are scanned like run:, so they fold the same way.
    [InlineData("      - uses: some/action@v1\n        with:\n          args: azd\n            up\n")]
    [InlineData("      - run: echo hi\n        env:\n          DEPLOY: \"az\n            login --identity\"\n")]
    public void FoldsMultiLinePlainAndQuotedScalars(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldNotBeEmpty(step);
    }

    [Fact]
    public void FoldedScalar_IsReportedAtItsFirstPhysicalLine()
    {
        PlantedWorkflowHits(OnPush("      - run: azd\n          up\n")).ShouldHaveSingleItem().Line.ShouldBe(6);
    }

    [Theory]
    // A blank line inside a plain or quoted scalar is a newline: `azd` (help) and `up` are two commands.
    [InlineData("      - run: azd\n\n          up\n")]
    [InlineData("      - run: \"azd\n\n          up\"\n")]
    // A sibling key at the key's own indent is not a continuation.
    [InlineData("      - run: azd\n        name: up\n")]
    // Display-only keys are prose, never commands, even when a continuation line reads like one.
    [InlineData("      - name: Deploy with\n          azd up\n        run: echo hi\n")]
    [InlineData("      - name: \"Deploy with\n          azd up\"\n        run: echo hi\n")]
    [InlineData("      - name: 'Deploy with\n          az login'\n        run: echo hi\n")]
    public void DoesNotFold_AcrossBlankLines_SiblingKeys_OrDisplayKeys(string step)
    {
        PlantedWorkflowHits(OnPush(step)).ShouldBeEmpty(step);
    }

    [Fact]
    public void TopLevelDisplayKeys_AreProse()
    {
        PlantedWorkflowHits("name: Release\n  azd up\nrun-name: Deploy\n  az login\n" + OnPush("      - run: echo hi\n")).ShouldBeEmpty();
        PlantedFileHits(".github/actions/x/action.yml", "name: x\ndescription: Wraps\n  azd up\nruns:\n  using: composite\n  steps:\n    - run: echo hi\n")
            .ShouldBeEmpty();
    }

    [Fact]
    public void KeyLikeLines_InsideAnotherKeysBlockScalar_AreNotReadAsKeys()
    {
        // `name:` here is script text in a literal block, not a display key, so its next line is still scanned.
        PlantedWorkflowHits(OnPush("      - uses: actions/github-script@v7\n        with:\n          script: |\n            name: x\n              azd up\n"))
            .ShouldNotBeEmpty();
    }

    // ---- A3: Markdown executed as a script ---------------------------------------------------------------------

    private const string AzDeploy = "az group create -n x -l y\n";

    [Theory]
    // Interpreters, by name, path or quoted, with options before the script.
    [InlineData("bash eng/x/deploy.md")]
    [InlineData("sh ./eng/x/deploy.md")]
    [InlineData("zsh eng/x/deploy.md")]
    [InlineData("dash eng/x/deploy.md")]
    [InlineData("/bin/bash eng/x/deploy.md")]
    [InlineData("\"bash\" eng/x/deploy.md")]
    [InlineData("bash -e -o pipefail eng/x/deploy.md")]
    [InlineData("pwsh -File eng/x/deploy.md")]
    [InlineData("pwsh -NoProfile -ExecutionPolicy Bypass -File eng/x/deploy.md")]
    [InlineData("powershell eng/x/deploy.md")]
    [InlineData("python eng/x/deploy.md")]
    [InlineData("python3 eng/x/deploy.md")]
    [InlineData("node eng/x/deploy.md")]
    [InlineData("source eng/x/deploy.md")]
    [InlineData(". eng/x/deploy.md")]
    [InlineData(". ./eng/x/DEPLOY.MD")]
    // Inline scripts are read as command lines in turn.
    [InlineData("bash -ec \"source eng/x/deploy.md\"")]
    [InlineData("pwsh -Command \". ./eng/x/deploy.md\"")]
    // Wrappers, assignments and command positions.
    [InlineData("env FOO=1 bash eng/x/deploy.md")]
    [InlineData("sudo -u runner bash eng/x/deploy.md")]
    [InlineData("exec bash eng/x/deploy.md")]
    [InlineData("command bash eng/x/deploy.md")]
    [InlineData("FOO=1 bash eng/x/deploy.md")]
    [InlineData("set -e; bash eng/x/deploy.md")]
    [InlineData("if true; then bash eng/x/deploy.md; fi")]
    [InlineData("test -f x && bash eng/x/deploy.md")]
    [InlineData("(cd eng/x && bash deploy.md)")]
    // Read, piped or redirected into an interpreter.
    [InlineData("cat eng/x/deploy.md | bash")]
    [InlineData("cat eng/x/deploy.md | sudo bash -s")]
    [InlineData("cat eng/x/deploy.md | python3 -")]
    [InlineData("tee /dev/null < eng/x/deploy.md | sh")]
    [InlineData("bash < eng/x/deploy.md")]
    [InlineData("< eng/x/deploy.md bash")]
    [InlineData("bash <(cat eng/x/deploy.md)")]
    [InlineData("bash -c \"$(cat eng/x/deploy.md)\"")]
    [InlineData("eval \"$(cat eng/x/deploy.md)\"")]
    [InlineData("iex (Get-Content eng/x/deploy.md -Raw)")]
    [InlineData("Get-Content eng/x/deploy.md -Raw | Invoke-Expression")]
    [InlineData("gc eng/x/deploy.md | iex")]
    // The Markdown file as the command word.
    [InlineData("./eng/x/deploy.md")]
    [InlineData("eng/x/deploy.md")]
    [InlineData("\"./eng/x/deploy.md\"")]
    [InlineData("chmod +x eng/x/deploy.md && ./eng/x/deploy.md")]
    public void FlagsMarkdownRunAsAScript(string command)
    {
        var hits = PlantedTreeHits((".github/workflows/x.yml", OnPush($"      - run: {command}\n")), ("eng/x/deploy.md", AzDeploy));

        hits.ShouldContain(h => h.Pattern == MarkdownExecution.Rule && h.File == ".github/workflows/x.yml", command);
    }

    [Theory]
    [InlineData(".github/workflows/x.yml", "on: push\njobs:\n  j:\n    steps:\n      - run: |\n          set -e\n          bash eng/x/deploy.md\n")]
    [InlineData(".github/workflows/x.yml", "on: push\njobs:\n  j:\n    steps:\n      - run: >\n          bash\n          eng/x/deploy.md\n")]
    [InlineData(".github/workflows/x.yml", "on: push\njobs:\n  j:\n    steps:\n      - run: bash\n          eng/x/deploy.md\n")]
    [InlineData(".github/actions/deploy/action.yml", "runs:\n  using: composite\n  steps:\n    - run: bash eng/x/deploy.md\n      shell: bash\n")]
    [InlineData(".github/scripts/release.sh", "bash ../../eng/x/deploy.md\n")]
    [InlineData("eng/x/run.sh", "source ./deploy.md\n")]
    [InlineData("eng/x/run.ps1", "& ./deploy.md\n")]
    [InlineData("eng/x/run.ps1", ". $PSScriptRoot/deploy.md\n")]
    public void FlagsMarkdownRunAsAScript_InEveryScriptForm(string path, string content)
    {
        PlantedTreeHits((path, content), ("eng/x/deploy.md", AzDeploy))
            .ShouldContain(h => h.Pattern == MarkdownExecution.Rule && h.File == path, path);
    }

    [Fact]
    public void ShellScriptControl_IsStillFlaggedThroughItsContent()
    {
        var hits = PlantedTreeHits((".github/workflows/x.yml", OnPush("      - run: bash eng/x/deploy.sh\n")), ("eng/x/deploy.sh", AzDeploy));

        hits.ShouldHaveSingleItem().File.ShouldBe("eng/x/deploy.sh");
    }

    [Theory]
    [InlineData("cat README.md")]
    [InlineData("markdownlint docs/*.md")]
    [InlineData("git add eng/x/README.md")]
    [InlineData("cp README.md _site/")]
    [InlineData("echo docs/*.md")]
    [InlineData("cat CHANGELOG.md | head -n 20")]
    [InlineData("cat README.md | node render.mjs")]
    [InlineData("node .github/scripts/validate-docs.mjs docs/guide.md")]
    [InlineData("python -m markdown README.md > _site/index.html")]
    [InlineData("python3 md2html.py < README.md > _site/index.html")]
    [InlineData("pwsh -File ./build.ps1 -Readme README.md")]
    [InlineData("bash -c \"cat README.md\"")]
    [InlineData("for f in docs/*.md; do node check.mjs \"$f\"; done")]
    public void DoesNotFlag_MarkdownAsData(string command)
    {
        PlantedWorkflowHits(OnPush($"      - run: {command}\n")).ShouldBeEmpty(command);
    }

    [Fact]
    public void DoesNotFlag_MarkdownPaths_OutsideRunContent_OrInNonScriptFiles()
    {
        // A Pages-style workflow names Markdown in triggers, step names and action inputs; none of it is a command.
        const string Pages =
            "name: Pages\non:\n  push:\n    branches: [main]\n    paths:\n      - '**/*.md'\n      - README.md\n  workflow_dispatch:\n" +
            "permissions:\n  contents: read\n  pages: write\n  id-token: write\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n" +
            "      - uses: actions/checkout@v4\n      - name: Render README.md\n        run: node build.mjs README.md docs/index.md\n" +
            "      - uses: actions/upload-pages-artifact@v3\n        with:\n          path: _site/index.md\n" +
            "  deploy:\n    needs: build\n    environment:\n      name: github-pages\n    runs-on: ubuntu-latest\n    steps:\n" +
            "      - uses: actions/deploy-pages@v4\n";
        PlantedWorkflowHits(Pages).ShouldBeEmpty();
        PlantedFileHits("eng/x/Files.cs", "    \"README.md\",\n").ShouldBeEmpty();
        PlantedFileHits("eng/x/files.json", "[\n  \"docs/README.md\",\n  \"x.md\"\n]\n").ShouldBeEmpty();
    }
}
