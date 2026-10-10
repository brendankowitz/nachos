$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TestResults.ps1')
$directory = Join-Path $PSScriptRoot "../../artifacts/ci/helper-tests/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$path = Join-Path $directory 'fixture.trx'
$count = 0
function Check {
    param([string] $Name, [scriptblock] $Body, [bool] $ShouldFail = $false)
    $failed = $false
    try { & $Body } catch { $failed = $true }
    if ($failed -ne $ShouldFail) { throw "CI helper regression: $Name (expected failure=$ShouldFail, actual=$failed)." }
    Write-Host "PASS $Name"
    $script:count++
}
function Write-Fixture {
    param([string] $Results, [string] $Counters)
    Set-Content $path "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Results>$Results</Results><ResultSummary><Counters $Counters /></ResultSummary></TestRun>"
}
try {
    Check 'missing TRX fails even when dotnet exits zero' {
        Assert-CiTestResult -Path $path -Suite fixture -NativeExitCode 0
    } $true
    Write-Fixture '' 'total="0" executed="0" passed="0" failed="0"'
    Check 'zero tests fails' { Assert-CiTestResult $path fixture 0 } $true
    Write-Fixture '<UnitTestResult testName="ok" outcome="Passed" />' 'total="1" executed="1" passed="1" failed="0"'
    Check 'actual passing result succeeds' { Assert-CiTestResult $path fixture 0 }
    Check 'raw runner failure remains failure' { Assert-CiTestResult $path fixture 3 } $true
    Write-Fixture '<UnitTestResult testName="bad" outcome="Failed" />' 'total="1" executed="1" passed="0" failed="1"'
    Check 'failed result cannot be masked by zero exit' { Assert-CiTestResult $path fixture 0 } $true
    Write-Fixture '<UnitTestResult testName="ok" outcome="Passed" />' 'total="2" executed="1" passed="1" failed="0"'
    Check 'incomplete results fail' { Assert-CiTestResult $path fixture 0 } $true
    Write-Fixture '<UnitTestResult testName="ok" outcome="Passed" /><UnitTestResult testName="hidden" outcome="NotExecuted"><Output><ErrorInfo><Message>unexpected</Message></ErrorInfo></Output></UnitTestResult>' 'total="2" executed="1" passed="1" failed="0"'
    Check 'unexpected skip fails' { Assert-CiTestResult $path fixture 0 } $true
    $reason = if ($IsWindows) { 'Not executed: Linux-only native open/statx evidence path.' }
        else { "Not executed: creating device nodes (mknod, CAP_MKNOD) in the test's own directory requires NACHOS_LINUX_DEVICE_FIXTURES=1." }
    $reason = [Security.SecurityElement]::Escape($reason)
    Write-Fixture "<UnitTestResult testName='ok' outcome='Passed' /><UnitTestResult testName='platform' outcome='NotExecuted'><Output><ErrorInfo><Message>$reason</Message></ErrorInfo></Output></UnitTestResult>" 'total="2" executed="1" passed="1" failed="0"'
    Check 'documented native platform skip remains explicit' { Assert-CiTestResult $path 'Nachos.LicenseCheck.Tests' 0 }
    Check 'platform skip cannot exempt a different suite' { Assert-CiTestResult $path 'Nachos.Api.Tests' 0 } $true
    $sdkOutput = "============================== 10 passed in 1.23s ==============================`nℹ tests 9`nℹ pass 9`nℹ fail 0`nℹ skipped 0`nℹ todo 0"
    Check 'both SDK language counts are required' { Assert-CiSdkResult $sdkOutput }
    Check 'empty SDK output fails' { Assert-CiSdkResult '' } $true
    Check 'SDK zero-test success fails' { Assert-CiSdkResult ($sdkOutput.Replace('tests 9', 'tests 0')) } $true
    Check 'SDK scoped skip fails' { Assert-CiSdkResult ($sdkOutput.Replace('skipped 0', 'skipped 1')) } $true
    Check 'SDK disabled-auth fallback fails' { Assert-CiSdkResult ($sdkOutput + "`nauth: disabled") } $true
    Check 'SDK xfail success fails' { Assert-CiSdkResult ($sdkOutput.Replace('10 passed', '9 passed, 1 xfailed')) } $true
    Check 'SDK count mismatch fails' { Assert-CiSdkResult ($sdkOutput.Replace('pass 9', 'pass 8')) } $true
    Check 'a disappeared Python scenario fails' { Assert-CiSdkResult ($sdkOutput.Replace('10 passed', '9 passed')) } $true
    $env:Nachos__SqlServer__ConnectionString = 'injected-test-value'
    $env:ConnectionStrings__nachos = 'injected-test-value'
    $env:AZURE_CLIENT_SECRET = 'injected-test-value'
    $env:NACHOS_CONFORMANCE_SECRET = 'injected-test-value'
    $env:UNRELATED_AMBIENT_CONFIGURATION = 'injected-test-value'
    Check 'child boundary removes ambient configuration and keeps tool discovery' {
        . (Join-Path $PSScriptRoot 'Enter-CiEnvironment.ps1')
        foreach ($name in @('Nachos__SqlServer__ConnectionString', 'ConnectionStrings__nachos', 'AZURE_CLIENT_SECRET', 'NACHOS_CONFORMANCE_SECRET', 'UNRELATED_AMBIENT_CONFIGURATION')) {
            if (Test-Path "Env:$name") { throw "Ambient $name survived." }
        }
        if (-not (Get-Command dotnet -CommandType Application)) { throw 'Lost dotnet tool discovery.' }
        if ($env:TEMP -notlike '*artifacts*ci*work') { throw 'Child scratch is not repository-local.' }
    }
    $pythonRoot = [IO.Path]::GetFullPath((Join-Path $directory 'selected-python'))
    $pythonBin = Join-Path $pythonRoot 'bin'
    $pythonLib = Join-Path $pythonRoot 'lib'
    New-Item -ItemType Directory -Path $pythonBin, $pythonLib -Force | Out-Null
    $selectedPython = Join-Path $pythonBin 'python'
    # Path-only fixtures: no fake interpreter is executed or claimed as runtime proof.
    New-Item -ItemType File -Path $selectedPython | Out-Null
    Check 'unselected loader paths and Python startup hooks are discarded' {
        $env:LD_LIBRARY_PATH = "$pythonLib$([IO.Path]::PathSeparator)untrusted-loader"
        $env:LD_PRELOAD = 'untrusted-library'
        $env:PYTHONPATH = 'untrusted-module'
        $env:PYTHONHOME = 'untrusted-home'
        $env:PYTHONSTARTUP = 'untrusted-startup'
        . (Join-Path $PSScriptRoot 'Enter-CiEnvironment.ps1')
        foreach ($name in @('LD_LIBRARY_PATH', 'LD_PRELOAD', 'PYTHONPATH', 'PYTHONHOME', 'PYTHONSTARTUP')) {
            if (Test-Path "Env:$name") { throw "Unselected $name survived." }
        }
    }
    Check 'selected interpreter resolves only its sibling library directory (path guard)' {
        . (Join-Path $PSScriptRoot 'PythonRuntime.ps1')
        if ((Get-CiPythonLibraryDirectory $selectedPython) -ne $pythonLib) {
            throw 'Selected library directory was lost or replaced.'
        }
    }
    . (Join-Path $PSScriptRoot 'PythonRuntime.ps1')
    Check 'relative interpreter paths fail (path guard)' {
        Get-CiPythonLibraryDirectory 'selected-python/bin/python'
    } $true
    Check 'missing selected interpreter fails (path guard)' {
        Get-CiPythonLibraryDirectory (Join-Path $pythonBin 'python3.13')
    } $true
    Check 'missing action output fails instead of silently disabling the context' {
        . (Join-Path $PSScriptRoot 'Enter-CiEnvironment.ps1') -PythonExecutable ''
    } $true
    Check 'an unrelated executable is not a Python runtime (path guard)' {
        $other = Join-Path $pythonBin 'unrelated'
        New-Item -ItemType File -Path $other | Out-Null
        Get-CiPythonLibraryDirectory $other
    } $true
    Check 'missing selected library directory fails (path guard)' {
        Remove-Item -LiteralPath $pythonLib
        try { Get-CiPythonLibraryDirectory $selectedPython }
        finally { New-Item -ItemType Directory -Path $pythonLib | Out-Null }
    } $true
    Check 'explicit Python context excludes ambient loaders across repeated clears (environment guard)' {
        foreach ($attempt in 1..2) {
            $env:LD_LIBRARY_PATH = 'untrusted-loader'
            $env:PYTHONPATH = 'untrusted-module'
            . (Join-Path $PSScriptRoot 'Enter-CiEnvironment.ps1') -PythonExecutable $selectedPython
            if ($IsLinux) {
                if ($env:LD_LIBRARY_PATH -ne $pythonLib) { throw 'Selected Linux library path was not reconstructed.' }
            }
            elseif (Test-Path Env:LD_LIBRARY_PATH) { throw 'Windows must not acquire a Linux loader path.' }
            if (Test-Path Env:PYTHONPATH) { throw 'An ambient Python module path survived.' }
        }
    }
    if ($IsWindows) {
        Check 'Windows path guards cannot stand in for Linux runtime execution' {
            Test-CiPythonRuntime $selectedPython
        } $true
    }
    Write-Host "CI helper checks: $count passed."
}
finally { Remove-Item -LiteralPath $directory -Recurse -Force }
