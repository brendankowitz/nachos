[CmdletBinding()]
param(
    [switch] $Publication
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location $root
. (Join-Path $PSScriptRoot 'Enter-CiEnvironment.ps1')
. (Join-Path $PSScriptRoot 'TestResults.ps1')

function Invoke-DocsCommand {
    param([string] $Tool, [string[]] $Arguments)
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed with exit code $LASTEXITCODE." }
}

$work = Join-Path $root 'artifacts/ci/docs'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$env:NUGET_PACKAGES ??= Join-Path $work 'nuget'
$privateHome = Join-Path $work 'home'
New-Item -ItemType Directory -Path $privateHome -Force | Out-Null
foreach ($name in @('HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'DOTNET_CLI_HOME')) {
    Set-Item "Env:$name" $privateHome
}
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:UseSharedCompilation = 'false'
$env:NACHOS_DOCSGEN_TEST_ROOT = $root
$env:DOCS_CONFIGURATION = 'Release'
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $root 'docs/site/.playwright'
$nugetConfig = Join-Path $privateHome 'NuGet.Config'
'<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>' |
    Set-Content $nugetConfig -Encoding utf8NoBOM

Invoke-DocsCommand dotnet @('restore', 'Nachos.slnx', '--configfile', $nugetConfig, '-m:1', '-nr:false')
Invoke-DocsCommand dotnet @('build', 'eng/ci/WorkflowYaml', '-c', 'Release', '--no-restore', '-warnaserror',
    '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
$yamlAssembly = Join-Path $root 'eng/ci/WorkflowYaml/bin/Release/net10.0/YamlDotNet.dll'
& (Join-Path $PSScriptRoot 'Test-WorkflowPolicy.ps1') -YamlAssembly $yamlAssembly
& (Join-Path $PSScriptRoot 'Test-DocsWorkflowPolicy.ps1') -YamlAssembly $yamlAssembly
Invoke-DocsCommand npm @('--prefix', 'docs/site', 'ci', '--ignore-scripts', '--no-audit', '--no-fund')
Invoke-DocsCommand npm @('--prefix', '.github/scripts', 'ci', '--ignore-scripts', '--no-audit', '--no-fund')
$browserArguments = @('--prefix', 'docs/site', 'run', 'browser:install')
if ($IsLinux) { $browserArguments += @('--', '--with-deps') }
Invoke-DocsCommand npm $browserArguments

if (-not $Publication) {
    foreach ($configuration in @('Debug', 'Release')) {
        $results = Join-Path $work "tests/$configuration"
        if (Test-Path $results) { throw "Refusing stale DocsGen results: $results" }
        Invoke-DocsCommand dotnet @('test', 'test/Nachos.DocsGen.Tests', '-c', $configuration, '--no-restore',
            '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '--logger', 'trx;LogFileName=results.trx',
            '--results-directory', $results)
        Assert-CiTestResult -Path (Join-Path $results 'results.trx') -Suite "DocsGen-$configuration" -NativeExitCode 0
    }
    Invoke-DocsCommand npm @('--prefix', 'docs/site', 'test')
    Invoke-DocsCommand npm @('--prefix', '.github/scripts', 'run', 'check')
    Invoke-DocsCommand node @('--test', '.github/scripts/docs-artifact.test.mjs')
}

# This is the one final build. Nothing after the seal is allowed to rebuild it.
Invoke-DocsCommand npm @('--prefix', 'docs/site', 'run', 'build')
$seal = Join-Path $work 'tree-seal.json'
$archive = Join-Path $work 'site.tar'
Invoke-DocsCommand node @('.github/scripts/docs-artifact.mjs', 'seal', $root, $seal)
Invoke-DocsCommand node @('.github/scripts/docs-artifact.mjs', 'archive', $root, $seal, $archive)
Invoke-DocsCommand node @('.github/scripts/docs-artifact.mjs', 'verify-archive', $root, $seal, $archive)
Write-Host 'Source-only artifact verified locally. Nothing uploaded or deployed.'
