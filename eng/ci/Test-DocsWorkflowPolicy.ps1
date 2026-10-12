param([Parameter(Mandatory)] [string] $YamlAssembly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
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
        @{ Name='write permission'; From='contents: read'; To='contents: write' },
        @{ Name='OIDC permission'; From='contents: read'; To="contents: read`n  id-token: write" },
        @{ Name='upload action'; From='actions/setup-node@'; To='actions/upload-artifact@' },
        @{ Name='retained credentials'; From='persist-credentials: false'; To='persist-credentials: true' },
        @{ Name='job permissions'; From="  source:`n"; To="  source:`n    permissions: write-all`n" },
        @{ Name='hidden audit failure'; From='run: ./eng/ci/Invoke-DocsSite.ps1 -Audit'; To="continue-on-error: true`n        run: ./eng/ci/Invoke-DocsSite.ps1 -Audit" }
    )) {
        $normalized = $original.Replace("`r`n", "`n")
        if (-not $normalized.Contains($mutation.From)) { throw "Mutation target missing: $($mutation.Name)" }
        $normalized.Replace($mutation.From, $mutation.To) | Set-Content $path -Encoding utf8NoBOM
        $rejected = $false
        try {
            & (Join-Path $PSScriptRoot 'Test-WorkflowPolicy.ps1') -YamlAssembly $YamlAssembly -SourceRoot $fixture | Out-Null
        }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Source-only policy accepted $($mutation.Name)." }
        $count++
    }
    Write-Host "Docs source-only policy: $count forbidden-workflow mutations rejected."
}
finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
