[CmdletBinding()]
param(
    [switch] $Audit,
    [switch] $Publication,
    [string] $PythonExecutable
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
if (($Audit -or $Publication) -and -not $PythonExecutable) { throw 'The full audit requires an explicitly selected Python interpreter.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location $root
if ($PythonExecutable) {
    . (Join-Path $PSScriptRoot 'Enter-CiEnvironment.ps1') -PythonExecutable $PythonExecutable
}
else {
    . (Join-Path $PSScriptRoot 'Enter-CiEnvironment.ps1')
}
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
Invoke-DocsCommand dotnet @('build', 'test/Nachos.LicenseCheck.Tests', '-c', 'Release', '--no-restore', '-warnaserror',
    '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
& (Join-Path $PSScriptRoot 'Test-WorkflowPolicy.ps1') -YamlAssembly (Join-Path $root 'eng/license-check/bin/Release/net10.0/YamlDotNet.dll')
& (Join-Path $PSScriptRoot 'Test-DocsWorkflowPolicy.ps1') -YamlAssembly (Join-Path $root 'eng/license-check/bin/Release/net10.0/YamlDotNet.dll')
Invoke-DocsCommand npm @('--prefix', 'docs/site', 'ci', '--ignore-scripts', '--no-audit', '--no-fund')
Invoke-DocsCommand npm @('--prefix', '.github/scripts', 'ci', '--ignore-scripts', '--no-audit', '--no-fund')
$browserArguments = @('--prefix', 'docs/site', 'run', 'browser:install')
if ($IsLinux) { $browserArguments += @('--', '--with-deps') }
Invoke-DocsCommand npm $browserArguments

if (-not $Audit) {
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

$checker = Join-Path $root 'eng/license-check/bin/Release/net10.0/Nachos.LicenseCheck.dll'
if ($Audit -or $Publication) {
    $npmArchives = Join-Path $work 'npm-archives'
    $pythonArchives = Join-Path $work 'python-archives'
    foreach ($producer in @('docs/site', '.github/scripts', 'test/conformance/typescript')) {
        if ($producer -eq 'test/conformance/typescript') {
            Invoke-DocsCommand npm @('--prefix', $producer, 'ci', '--ignore-scripts', '--no-audit', '--no-fund')
        }
        Invoke-DocsCommand dotnet @($checker, 'fetch-npm', '--lock', "$producer/package-lock.json", '--npm-archives', $npmArchives)
    }
    $projection = & dotnet $checker project-python --lock test/conformance/python/requirements.lock
    if ($LASTEXITCODE -ne 0) { throw 'Python lock projection failed.' }
    $projectionPath = Join-Path $work 'requirements.txt'
    $projection | Set-Content $projectionPath -Encoding utf8NoBOM
    Invoke-DocsCommand $PythonExecutable @('-m', 'pip', 'download', '--disable-pip-version-check',
        '--require-hashes', '--only-binary=:all:', '--no-deps', '-r', $projectionPath, '--dest', $pythonArchives)
    foreach ($product in @('Api', 'Cli')) {
        Invoke-DocsCommand dotnet @('publish', "src/Nachos.$product", '-c', 'Release', '--no-restore',
            '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-o', (Join-Path $work "publish/$product"))
    }
}

# This is the one final build. Nothing after the seal is allowed to rebuild it.
Invoke-DocsCommand npm @('--prefix', 'docs/site', 'run', 'build')
$seal = Join-Path $work 'tree-seal.json'
$archive = Join-Path $work 'site.tar'
Invoke-DocsCommand node @('.github/scripts/docs-artifact.mjs', 'seal', $root, $seal)
Invoke-DocsCommand dotnet @($checker, 'verify-docs', '--site-root', 'docs/site', '--asset-root', 'docs/assets',
    '--npm-root', 'docs/site', '--output-root', 'docs/site/dist',
    '--report', (Join-Path $work 'provenance-report.json'))

if ($Audit -or $Publication) {
    # The production checker discovers the site lock/dist; this is a full audit,
    # not the green verify-docs protocol check above. Preserve its raw failure.
    & (Join-Path $root 'eng/license-check/Check-Licenses.ps1') `
        -ApiPublishRoot (Join-Path $work 'publish/Api') -CliPublishRoot (Join-Path $work 'publish/Cli') `
        -NpmArchives $npmArchives -PythonArchives $pythonArchives -OutputDirectory (Join-Path $work 'audit')
    $auditExit = $LASTEXITCODE
    if ($Audit -and $auditExit -ne 0) { exit $auditExit }
    if ($Publication) {
        Write-Host "Full audit completed with exit code $auditExit; publication gate will evaluate the emitted docs artifact only."
        Invoke-DocsCommand dotnet @($checker, 'verify-publication',
            '--repo', $root,
            '--report', (Join-Path $work 'audit/license-report.json'),
            '--docs-report', (Join-Path $work 'provenance-report.json'),
            '--site-output', 'docs/site/dist')
    }
}

Invoke-DocsCommand node @('.github/scripts/docs-artifact.mjs', 'archive', $root, $seal, $archive)
Invoke-DocsCommand node @('.github/scripts/docs-artifact.mjs', 'verify-archive', $root, $seal, $archive)
Write-Host 'Source-only artifact verified locally. Nothing uploaded or deployed.'
