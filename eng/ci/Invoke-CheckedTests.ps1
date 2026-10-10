[CmdletBinding()]
param([Parameter(Mandatory)] [ValidateSet('Portable', 'Sql', 'Schema', 'Infra')] [string] $Slice)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TestResults.ps1')

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$portableProjects = @(
    'Nachos.Abstractions.Tests', 'Nachos.Api.Tests', 'Nachos.Architecture.Tests',
    'Nachos.Client.Tests', 'Nachos.Core.Tests', 'Nachos.DataLayer.InMemory.Tests',
    'Nachos.LicenseCheck.Tests', 'Nachos.DataLayer.SqlServer.Tests',
    'Nachos.Cli.Tests', 'Nachos.AppHost.Tests'
)
[xml] $solution = Get-Content (Join-Path $root 'Nachos.slnx') -Raw
$declaredTests = @($solution.SelectNodes('//Project/@Path') | Where-Object {
    $_.Value.StartsWith('test/') -and $_.Value.EndsWith('.csproj') -and
    $_.Value -ne 'test/Nachos.Testing/Nachos.Testing.csproj'
} | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Value) })
# Nachos.Testing explicitly declares IsTestProject=false. Every other test
# project must have a lane; adding a project requires updating this routing.
if (Compare-Object ($portableProjects + 'Nachos.Infra.Tests' | Sort-Object) ($declaredTests | Sort-Object)) {
    throw 'Solution test projects changed: update the complete CI lane mapping.'
}
$filters = @{}
switch ($Slice) {
    Portable {
        $projects = $portableProjects
        # These mixed projects declare no Category traits. Their complete, unfiltered
        # projects also run in Sql; a failing/new Docker test is never excluded there.
        $sqlPortable = @(
            'DacpacCatalogTests', 'DatabaseOptionsTests', 'DeployReportClassifierTests',
            'DeployScriptAnalysisTests', 'NoEfMigrationsTests', 'SchemaDeployerGuardTests',
            'SchemaGateTests', 'SqlFilterCompilerTests'
        )
        $filters['Nachos.DataLayer.SqlServer.Tests'] = ($sqlPortable | ForEach-Object {
            "FullyQualifiedName~Nachos.DataLayer.SqlServer.Tests.$_."
        }) -join '|'
        $filters['Nachos.Cli.Tests'] = 'FullyQualifiedName!~.GrantCommandTests.&FullyQualifiedName!~.SchemaCommandTests.&FullyQualifiedName!~.ServerErrorDisclosureTests.'
        $filters['Nachos.AppHost.Tests'] = 'FullyQualifiedName!~.AppHostSmokeTests.'
    }
    Sql { $projects = @('Nachos.DataLayer.SqlServer.Tests', 'Nachos.Cli.Tests', 'Nachos.AppHost.Tests') }
    Schema {
        $projects = @('Nachos.DataLayer.SqlServer.Tests', 'Nachos.Cli.Tests')
        $filters['Nachos.DataLayer.SqlServer.Tests'] = 'FullyQualifiedName~.DacpacCatalogTests.|FullyQualifiedName~.DatabaseOptionsTests.|FullyQualifiedName~.SchemaDeployerTests.|FullyQualifiedName~.SqlSchemaTransitionTests.'
        $filters['Nachos.Cli.Tests'] = 'FullyQualifiedName~.SchemaCommandTests.'
    }
    Infra { $projects = @('Nachos.Infra.Tests') }
}
$failedSuites = 0
foreach ($project in $projects) {
    try {
        $projectPath = Join-Path $root "test/$project/$project.csproj"
        $results = Join-Path $root "artifacts/ci/tests/$Slice/$project"
        if (Test-Path $results) { throw "Refusing stale test results: $results" }
        New-Item -ItemType Directory -Path $results | Out-Null
        $arguments = @(
            'test', $projectPath, '-c', 'Release', '--no-build', '--no-restore',
            '--logger', 'trx;LogFileName=results.trx', '--logger', 'console;verbosity=normal',
            '--results-directory', $results
        )
        if ($filters.ContainsKey($project)) { $arguments += @('--filter', $filters[$project]) }
        Write-Host "TEST $project / $Slice / filter=$(if ($filters.ContainsKey($project)) { $filters[$project] } else { '<all>' })"
        & dotnet @arguments
        $nativeExit = $LASTEXITCODE
        Assert-CiTestResult -Path (Join-Path $results 'results.trx') -Suite $project -NativeExitCode $nativeExit
    }
    catch {
        # Run every selected suite, but make any failure fail this step and job.
        [Console]::Error.WriteLine($_.Exception.Message)
        $failedSuites++
    }
}
if ($failedSuites) { throw "$failedSuites suite(s) failed in $Slice." }
