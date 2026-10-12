param([Parameter(Mandatory)] [string] $YamlAssembly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Invoke-DocsSite.ps1'), [ref]$null, [ref]$null)
$reservedAssignments = $runnerAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
    $node.Left.VariablePath.UserPath -ieq 'HOME'
}, $true)
if ($reservedAssignments.Count) { throw 'The docs runner must not assign PowerShell read-only $HOME; use a named private-home variable.' }
$fixture = Join-Path $root "artifacts/ci/docs-policy/$([guid]::NewGuid().ToString('N'))"
$workflows = Join-Path $fixture '.github/workflows'
New-Item -ItemType Directory -Path $workflows -Force | Out-Null
try {
    foreach ($name in @('ci.yml', 'docs-validate.yml', 'docs-site.yml')) {
        Copy-Item (Join-Path $root ".github/workflows/$name") $workflows
    }
    $path = Join-Path $workflows 'docs-site.yml'
    $original = Get-Content $path -Raw
    & (Join-Path $PSScriptRoot 'Test-WorkflowPolicy.ps1') -YamlAssembly $YamlAssembly -SourceRoot $fixture
    $count = 0
    foreach ($mutation in @(
        @{ Name='workflow pages permission'; File='docs-site.yml'; From='permissions:'; To="permissions:`n  pages: write" },
        @{ Name='workflow OIDC permission'; File='docs-site.yml'; From='permissions:'; To="permissions:`n  id-token: write" },
        @{ Name='source job pages permission'; File='docs-site.yml'; From="  source:`n"; To="  source:`n    permissions:`n      pages: write`n" },
        @{ Name='pages job OIDC permission'; File='docs-site.yml'; From="  pages:`n    name: Publication gate and Pages artifact"; To="  pages:`n    name: Publication gate and Pages artifact`n    permissions:`n      id-token: write" },
        @{ Name='deploy extra permission'; File='docs-site.yml'; From="      id-token: write`n"; To="      id-token: write`n      contents: write`n" },
        @{ Name='deploy missing environment'; File='docs-site.yml'; From="    environment:`n      name: github-pages`n      url: `${{ steps.deployment.outputs.page_url }}`n"; To='' },
        @{ Name='deploy unguarded if'; File='docs-site.yml'; From="    if: `${{ (github.event_name == 'push' || github.event_name == 'workflow_dispatch') && github.ref == 'refs/heads/main' }}`n"; To='' },
        @{ Name='deploy checkout step'; File='docs-site.yml'; From="    steps:`n      - id: deployment"; To="    steps:`n      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1`n      - id: deployment" },
        @{ Name='deploy run step'; File='docs-site.yml'; From="    steps:`n      - id: deployment"; To="    steps:`n      - run: echo no`n      - id: deployment" },
        @{ Name='deploy step inputs'; File='docs-site.yml'; From="      - id: deployment`n        uses: actions/deploy-pages@368f82528645a54fb793d4d04e342629a3f51346"; To="      - id: deployment`n        with:`n          preview: true`n        uses: actions/deploy-pages@368f82528645a54fb793d4d04e342629a3f51346" },
        @{ Name='deploy step continue on error'; File='docs-site.yml'; From="      - id: deployment`n        uses: actions/deploy-pages@368f82528645a54fb793d4d04e342629a3f51346"; To="      - id: deployment`n        continue-on-error: true`n        uses: actions/deploy-pages@368f82528645a54fb793d4d04e342629a3f51346" },
        @{ Name='deploy container'; File='docs-site.yml'; From="  deploy:`n    name: Deploy GitHub Pages"; To="  deploy:`n    name: Deploy GitHub Pages`n    container: alpine" },
        @{ Name='deploy services'; File='docs-site.yml'; From="  deploy:`n    name: Deploy GitHub Pages"; To="  deploy:`n    name: Deploy GitHub Pages`n    services:`n      postgres:`n        image: postgres" },
        @{ Name='deploy self-hosted runner'; File='docs-site.yml'; From="  deploy:`n    name: Deploy GitHub Pages`n    runs-on: ubuntu-24.04"; To="  deploy:`n    name: Deploy GitHub Pages`n    runs-on: self-hosted" },
        @{ Name='pages continue on error'; File='docs-site.yml'; From="  pages:`n    name: Publication gate and Pages artifact"; To="  pages:`n    name: Publication gate and Pages artifact`n    continue-on-error: true" },
        @{ Name='deploy environment string'; File='docs-site.yml'; From="    environment:`n      name: github-pages`n      url: `${{ steps.deployment.outputs.page_url }}`n"; To="    environment: github-pages`n" },
        @{ Name='workflow write all'; File='docs-site.yml'; From="permissions:`n  contents: read"; To='permissions: write-all' },
        @{ Name='deploy job write all'; File='docs-site.yml'; From="    permissions:`n      pages: write`n      id-token: write"; To='    permissions: write-all' },
        @{ Name='deploy reusable workflow'; File='docs-site.yml'; From="  deploy:`n    name: Deploy GitHub Pages"; To="  deploy:`n    name: Deploy GitHub Pages`n    uses: ./.github/workflows/reusable.yml" },
        @{ Name='tag-pinned deploy'; File='docs-site.yml'; From='actions/deploy-pages@368f82528645a54fb793d4d04e342629a3f51346'; To='actions/deploy-pages@v5.0.1' },
        @{ Name='tag-pinned pages upload'; File='docs-site.yml'; From='actions/upload-pages-artifact@fc324d3547104276b827a68afc52ff2a11cc49c9'; To='actions/upload-pages-artifact@v5.0.0' },
        @{ Name='pages upload missing condition'; File='docs-site.yml'; From="        if: `${{ (github.event_name == 'push' || github.event_name == 'workflow_dispatch') && github.ref == 'refs/heads/main' }}`n        with:"; To='        with:' },
        @{ Name='pages upload changed path'; File='docs-site.yml'; From='path: docs/site/dist'; To='path: docs/site' },
        @{ Name='pages upload missing inputs'; File='docs-site.yml'; From="        with:`n          path: docs/site/dist`n"; To='' },
        @{ Name='pages upload extra input'; File='docs-site.yml'; From='path: docs/site/dist'; To="path: docs/site/dist`n          retention-days: 1" },
        @{ Name='source upload-pages action'; File='docs-site.yml'; From='actions/setup-node@820762786026740c76f36085b0efc47a31fe5020'; To='actions/upload-pages-artifact@fc324d3547104276b827a68afc52ff2a11cc49c9' },
        @{ Name='deploy needs source only'; File='docs-site.yml'; From='needs: [source, pages]'; To='needs: source' },
        @{ Name='generic upload action'; File='docs-site.yml'; From='actions/setup-node@820762786026740c76f36085b0efc47a31fe5020'; To='actions/upload-artifact@65c4c4a1ddee5b72f698fdd19549f0f0fb45cf08' },
        @{ Name='ci workflow pages permission'; File='ci.yml'; From='contents: read'; To="contents: read`n  pages: write" },
        @{ Name='docs validate OIDC permission'; File='docs-validate.yml'; From='contents: read'; To="contents: read`n  id-token: write" },
        @{ Name='retained credentials'; File='docs-site.yml'; From='persist-credentials: false'; To='persist-credentials: true' },
        @{ Name='hidden audit failure'; File='docs-site.yml'; From='run: ./eng/ci/Invoke-DocsSite.ps1 -Audit'; To="continue-on-error: true`n        run: ./eng/ci/Invoke-DocsSite.ps1 -Audit" }
    )) {
        Copy-Item (Join-Path $root ".github/workflows/$($mutation.File)") (Join-Path $workflows $mutation.File) -Force
        $normalized = $original.Replace("`r`n", "`n")
        if ($mutation.File -ne 'docs-site.yml') {
            $normalized = (Get-Content (Join-Path $workflows $mutation.File) -Raw).Replace("`r`n", "`n")
        }
        if (-not $normalized.Contains($mutation.From)) { throw "Mutation target missing: $($mutation.Name)" }
        $normalized.Replace($mutation.From, $mutation.To) | Set-Content (Join-Path $workflows $mutation.File) -Encoding utf8NoBOM
        $rejected = $false
        try {
            & (Join-Path $PSScriptRoot 'Test-WorkflowPolicy.ps1') -YamlAssembly $YamlAssembly -SourceRoot $fixture | Out-Null
        }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Source-only policy accepted $($mutation.Name)." }
        Copy-Item (Join-Path $root ".github/workflows/$($mutation.File)") (Join-Path $workflows $mutation.File) -Force
        $count++
    }
    Set-Content (Join-Path $workflows 'unreviewed.yml') -Encoding utf8NoBOM -Value @'
name: Unreviewed
on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:
concurrency:
  group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}
permissions:
  contents: read
jobs:
  minted-token:
    runs-on: ubuntu-24.04
    permissions:
      id-token: write
    steps:
      - run: echo no
'@
    $rejected = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-WorkflowPolicy.ps1') -YamlAssembly $YamlAssembly -SourceRoot $fixture | Out-Null
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Source-only policy accepted new workflow OIDC grant.' }
    $count++
    Write-Host "Docs source-only policy: $count forbidden-workflow mutations rejected."
}
finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
