[CmdletBinding()]
param(
    [switch] $OfflineInfra,
    [ValidateNotNullOrEmpty()] [string] $PythonExecutable
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# Keep tool discovery, not application configuration or runner/service credentials.
$keep = @(
    'PATH', 'PATHEXT', 'SystemRoot', 'WINDIR', 'COMSPEC', 'SYSTEMDRIVE',
    'HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'PROGRAMFILES', 'PROGRAMFILES(X86)',
    'PROGRAMDATA', 'PSModulePath', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT(x86)', 'DOTNET_HOST_PATH',
    'NUGET_PACKAGES', 'GITHUB_WORKSPACE', 'GITHUB_STEP_SUMMARY', 'LANG', 'LC_ALL', 'TZ'
)
foreach ($entry in @(Get-ChildItem Env:)) {
    if ($entry.Name -notin $keep) { Remove-Item -LiteralPath "Env:$($entry.Name)" }
}
if ($PythonExecutable -and $IsLinux) {
    # Only the pinned setup-python action's explicit python-path output is trusted.
    # Never retain ambient LD_LIBRARY_PATH, PYTHONPATH, or interpreter startup hooks.
    . (Join-Path $PSScriptRoot 'PythonRuntime.ps1')
    $env:LD_LIBRARY_PATH = Get-CiPythonLibraryDirectory $PythonExecutable
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:CI = 'true'
$env:NO_PROXY = '127.0.0.1,localhost'
$env:no_proxy = $env:NO_PROXY
$work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/ci/work'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$env:TMP = $work
$env:TEMP = $work
$env:TMPDIR = $work

if ($OfflineInfra) {
    if ($IsWindows) {
        # Windows az.cmd lives in a dedicated directory; never invoke it to probe availability.
        $azureCmd = 'az.cmd'
        $azureExe = 'az.exe'
        $env:PATH = ($env:PATH.Split([IO.Path]::PathSeparator) | Where-Object {
            $_ -and -not (Test-Path (Join-Path $_ $azureCmd)) -and -not (Test-Path (Join-Path $_ $azureExe))
        }) -join [IO.Path]::PathSeparator
    }
    else {
        # Hosted Linux can put az in /usr/bin. Give the test host a tool-only PATH so
        # BicepCli.Resolve selects standalone bicep, not the real Azure CLI.
        $bin = Join-Path $work 'offline-bin'
        New-Item -ItemType Directory -Path $bin -Force | Out-Null
        foreach ($name in @(
            'bash', 'bicep', 'cat', 'chmod', 'cp', 'curl', 'cut', 'date', 'dirname',
            'docker', 'dotnet', 'env', 'grep', 'head', 'ls', 'mkdir', 'mktemp',
            'mv', 'openssl', 'pwsh', 'readlink', 'rm', 'sed', 'seq', 'sh', 'sleep',
            'sort', 'tail', 'tr', 'uname', 'wc'
        )) {
            $tool = Get-Command $name -CommandType Application -ErrorAction Stop | Select-Object -First 1
            $link = Join-Path $bin $name
            if (-not (Test-Path $link)) { New-Item -ItemType SymbolicLink -Path $link -Target $tool.Source | Out-Null }
        }
        $env:PATH = $bin
        $env:NACHOS_REQUIRE_INFRA_TOOLS = '1'
    }
    $azureCli = 'az'
    if (Get-Command $azureCli -CommandType Application -ErrorAction SilentlyContinue) {
        throw 'The offline infrastructure host must not resolve a real Azure CLI.'
    }
}
