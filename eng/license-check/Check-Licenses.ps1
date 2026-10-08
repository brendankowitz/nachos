[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [Parameter(Mandatory)] [string] $ApiPublishRoot,
    [Parameter(Mandatory)] [string] $CliPublishRoot,
    [string] $PythonArchives,
    [string] $NpmArchives,
    [string] $NugetInventory,
    [string] $NugetCache,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if (-not $NugetInventory) {
    $NugetInventory = Join-Path $OutputDirectory 'nuget-inventory.json'
    $inventory = & dotnet list (Join-Path $RepositoryRoot 'Nachos.slnx') package --include-transitive --format json --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet list package failed with exit code $LASTEXITCODE; inventory is incomplete."
    }
    $inventory | Set-Content -LiteralPath $NugetInventory -Encoding utf8NoBOM
}
if (-not $NugetCache) {
    $cacheResult = & dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0 -or $cacheResult -notmatch '^global-packages:\s*(.+)$') {
        throw 'Unable to locate the NuGet global-packages directory.'
    }
    $NugetCache = $Matches[1].Trim()
}
$checker = Join-Path $PSScriptRoot "bin/$Configuration/net10.0/Nachos.LicenseCheck.dll"
if (-not (Test-Path -LiteralPath $checker -PathType Leaf)) {
    throw 'Build test/Nachos.LicenseCheck.Tests (or the solution) before auditing; the checker does not restore or install dependencies.'
}
$arguments = @(
    $checker, '--repo', $RepositoryRoot, '--nuget-inventory', $NugetInventory,
    '--nuget-cache', $NugetCache, '--api-publish', $ApiPublishRoot, '--cli-publish', $CliPublishRoot,
    '--report', (Join-Path $OutputDirectory 'license-report.json')
)
if ($PythonArchives) {
    $arguments += @('--python-archives', $PythonArchives)
}
if ($NpmArchives) {
    $arguments += @('--npm-archives', $NpmArchives)
}
& dotnet @arguments
exit $LASTEXITCODE
