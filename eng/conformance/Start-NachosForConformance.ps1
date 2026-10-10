# UNVERIFIED on Windows by its author: the only host available was Linux, where this script was exercised under
# pwsh 7.6.6 (a dotnet tool) with -Run -AllowAuthDisabled. The reference implementation is
# start-nachos-for-conformance.sh; this is a deliberately thin twin with the same flags and behaviour (see that file's
# header for the strict auth probe and the environment file contents). Authentication is strict by default: a refused
# admin probe is an error and no suite runs. -RequireAuth selects that default explicitly, so a CI line documents itself
# (`-Run -RequireAuth`); combining it with -AllowAuthDisabled is a usage error (exit 2). -AllowAuthDisabled is the
# development-only fallback for a branch where auth is not published yet and applies only to the fail-closed answers
# 401 and 501; any other probe result (5xx, no connection, timeout) is a hard failure.
#
# Hermetic: every dotnet process that can start application code runs inside an allow-listed environment, so an ambient
# SQL, Key Vault, Entra, Azure, auth or telemetry setting cannot redirect or break the in-memory, offline run. That
# includes the builds: building src/Nachos.Api runs its OpenAPI document generation, which starts the application's
# startup code. Each child's environment is built from scratch (ProcessStartInfo.Environment is cleared, then filled);
# the caller's own environment is never modified for them.
#   * API and CLI (runtime) steps see: PATH, HOME, DOTNET_ROOT, LANG, LC_ALL, TMPDIR, SystemRoot, USERPROFILE, TEMP,
#     TMP, DOTNET_CLI_TELEMETRY_OPTOUT=1, DOTNET_NOLOGO=1, plus the synthetic settings this script sets itself
#     (ASPNETCORE_ENVIRONMENT, ASPNETCORE_URLS, Logging__LogLevel__Default, Nachos__Auth__*; the CLI gets only the
#     signing secret variable).
#   * build steps (dotnet build, dotnet msbuild -getProperty) see the runtime list plus, only when set, package-feed
#     access: HTTP_PROXY, HTTPS_PROXY, NO_PROXY, ALL_PROXY and their lower-case forms, SSL_CERT_FILE, SSL_CERT_DIR,
#     REQUESTS_CA_BUNDLE, NUGET_PACKAGES, NUGET_HTTP_CACHE_PATH, DOTNET_NUGET_SIGNATURE_VERIFICATION, DOTNET_CLI_HOME,
#     and, for Windows (build steps only, never the API or CLI): APPDATA, LOCALAPPDATA, ProgramData, ALLUSERSPROFILE,
#     PUBLIC, ProgramFiles, ProgramFiles(x86), ProgramW6432, CommonProgramFiles, CommonProgramFiles(x86),
#     CommonProgramW6432, HOMEDRIVE, HOMEPATH, SystemDrive, windir, ComSpec, PATHEXT, OS, USERNAME, USERDOMAIN,
#     COMPUTERNAME, NUMBER_OF_PROCESSORS, PROCESSOR_ARCHITECTURE, PROCESSOR_IDENTIFIER, PROCESSOR_LEVEL,
#     PROCESSOR_REVISION.
#     UNCONFIRMED: the Windows additions are a first hypothesis of the OS prerequisites NuGet and MSBuild need (the
#     first protected build on Windows failed with "Value cannot be null. (Parameter 'path1')" while loading NuGet
#     settings); they must be confirmed on a Windows host. If a build still fails, bisect by adding names one at a time
#     to $buildEnvironment, never by passing the caller's whole environment.
#     Never Nachos__*, ConnectionStrings__*, SQLAZURECONNSTR_*, SQLCONNSTR_*, AZURE_*, ASPNETCORE_*, DOTNET_ENVIRONMENT,
#     DOTNET_STARTUP_HOOKS, APPLICATIONINSIGHTS_* or OTEL_*.
#   * the suites (pip, npm, pytest, node) keep the caller's environment; their NACHOS_* settings are restored in a
#     finally block.
#
# Usage: Start-NachosForConformance.ps1 [-Run] [-RequireAuth | -AllowAuthDisabled] [-EnvFile <path>] [-TimeoutSeconds <n>]
#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$RequireAuth,
    [switch]$AllowAuthDisabled,
    [string]$EnvFile,
    [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'

if ($RequireAuth -and $AllowAuthDisabled) {
    [Console]::Error.WriteLine('error: -RequireAuth and -AllowAuthDisabled contradict each other; pass at most one')
    exit 2
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$workDir = Join-Path ([IO.Path]::GetTempPath()) ("nachos-conformance." + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $workDir | Out-Null
if (-not $EnvFile) { $EnvFile = Join-Path $workDir 'conformance.env' }
$apiProcess = $null

function Stop-Api {
    if ($script:apiProcess -and -not $script:apiProcess.HasExited) {
        $script:apiProcess.Kill($true)
        $script:apiProcess.WaitForExit()
    }
    $script:apiProcess = $null
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# The names a runtime child process (API, CLI) may inherit. Everything else the caller exported is dropped (same list
# as the bash twin, plus the Windows basics .NET needs).
$allowedEnvironment = 'PATH', 'HOME', 'DOTNET_ROOT', 'LANG', 'LC_ALL', 'TMPDIR', 'SystemRoot', 'USERPROFILE', 'TEMP', 'TMP'
# What a build step may additionally inherit, when the caller has it set: package-feed access, then Windows OS and
# profile prerequisites (NuGet and MSBuild resolve well-known folders from them and throw on an empty one). Names are
# copied only when set, so on Linux the Windows names simply do nothing; on Windows the environment is
# case-insensitive. Nothing application- or credential-related belongs here.
$buildEnvironment = 'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'no_proxy', 'all_proxy',
    'SSL_CERT_FILE', 'SSL_CERT_DIR', 'REQUESTS_CA_BUNDLE', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH',
    'DOTNET_NUGET_SIGNATURE_VERIFICATION', 'DOTNET_CLI_HOME',
    'APPDATA', 'LOCALAPPDATA', 'ProgramData', 'ALLUSERSPROFILE', 'PUBLIC',
    'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'CommonProgramFiles', 'CommonProgramFiles(x86)', 'CommonProgramW6432',
    'HOMEDRIVE', 'HOMEPATH', 'SystemDrive', 'windir', 'ComSpec', 'PATHEXT', 'OS', 'USERNAME', 'USERDOMAIN', 'COMPUTERNAME',
    'NUMBER_OF_PROCESSORS', 'PROCESSOR_ARCHITECTURE', 'PROCESSOR_IDENTIFIER', 'PROCESSOR_LEVEL', 'PROCESSOR_REVISION'

# A `dotnet <arguments>` start description whose environment is cleared and rebuilt from the allow-list, the names in
# $inherit and $settings.
function New-HermeticStartInfo([string[]]$arguments, [hashtable]$settings = @{}, [string[]]$inherit = @(), [string]$workingDirectory = $PWD.Path) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = $workingDirectory
    $info.UseShellExecute = $false
    $info.Environment.Clear()
    foreach ($name in $allowedEnvironment + $inherit) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $info.Environment[$name] = $value }
    }
    $info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $info.Environment['DOTNET_NOLOGO'] = '1'
    foreach ($name in $settings.Keys) { $info.Environment[$name] = $settings[$name] }
    $info
}

# Runs a build-tier dotnet command in the hermetic environment. Returns its trimmed standard output with -Capture;
# otherwise the output goes to the console. Throws on a non-zero exit code.
function Invoke-BuildStep([string]$what, [string[]]$arguments, [switch]$Capture) {
    $info = New-HermeticStartInfo $arguments -inherit $buildEnvironment
    if ($Capture) { $info.RedirectStandardOutput = $true }
    $process = [Diagnostics.Process]::Start($info)
    $output = if ($Capture) { $process.StandardOutput.ReadToEndAsync() }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "$what failed (exit code $($process.ExitCode))" }
    if ($Capture) { $output.GetAwaiter().GetResult().Trim() }
}

function Get-TargetPath([string]$project) {
    Invoke-BuildStep "locating $project" @('msbuild', (Join-Path $repoRoot "src/$project/$project.csproj"), '-p:Configuration=Release', '-getProperty:TargetPath') -Capture
}

# Runs $body with $settings applied to this process's environment and always restores the previous values.
function Invoke-WithEnvironment([hashtable]$settings, [scriptblock]$body) {
    $saved = @{}
    foreach ($name in $settings.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $settings[$name])
    }
    try { & $body }
    finally {
        # PowerShell turns a plain $null into "", which would leave an empty variable behind; NullString removes it.
        foreach ($name in $saved.Keys) {
            $previous = if ($null -eq $saved[$name]) { [NullString]::Value } else { $saved[$name] }
            [Environment]::SetEnvironmentVariable($name, $previous)
        }
    }
}

function New-Key([string[]]$keyArguments) {
    $info = New-HermeticStartInfo (@($cliDll, 'keys', 'create', '--signing-secret-env', 'NACHOS_CONFORMANCE_SECRET', '--kid', 'dev') + $keyArguments) `
        @{ NACHOS_CONFORMANCE_SECRET = $conformanceSecret } -workingDirectory (Split-Path $cliDll)
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "minting a key failed: $($errors.GetAwaiter().GetResult().Trim())" }
    $output.GetAwaiter().GetResult().Trim()
}

# Starts the API and waits for /health/ready. Returns the base URL. The port is picked up front (a tiny race, unlike
# port 0 in the bash twin) because the API's output stays on the console instead of being parsed for its address.
function Start-Api([bool]$authEnabled) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $baseUrl = "http://127.0.0.1:$port"

    $info = New-HermeticStartInfo @($apiDll) -workingDirectory (Split-Path $apiDll) -settings @{
        ASPNETCORE_ENVIRONMENT                     = 'Development'
        ASPNETCORE_URLS                            = $baseUrl
        Logging__LogLevel__Default                 = 'Warning'
        Nachos__Auth__Enabled                      = "$authEnabled".ToLowerInvariant()
        Nachos__Auth__NachosKey__Keys__0__Kid      = 'dev'
        Nachos__Auth__NachosKey__Keys__0__Secret   = $conformanceSecret
    }
    $script:apiProcess = [Diagnostics.Process]::Start($info)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:apiProcess.HasExited) { throw 'the API exited during startup' }
        try {
            Invoke-WebRequest "$baseUrl/health/ready" -UseBasicParsing -TimeoutSec 2 | Out-Null
            return $baseUrl
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    throw "the API was not ready within $TimeoutSeconds s"
}

try {
    foreach ($project in 'Nachos.Api', 'Nachos.Cli') {
        Invoke-BuildStep "building $project" @('build', (Join-Path $repoRoot "src/$project/$project.csproj"), '-c', 'Release', '-v', 'q', '--nologo')
    }
    $apiDll = Get-TargetPath 'Nachos.Api'
    $cliDll = Get-TargetPath 'Nachos.Cli'

    # The signing secret is handed only to the API and CLI processes; it is never printed or written.
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $conformanceSecret = [Convert]::ToBase64String($bytes)

    $adminKey = New-Key @('--admin')
    $peerKey = New-Key @('--workspace', 'conformance-auth', '--peer', 'member')

    $authMode = 'enforced'
    $baseUrl = Start-Api $true
    $status = try {
        (Invoke-WebRequest "$baseUrl/v3/workspaces/list" -Method Post -Body '{}' -ContentType 'application/json' `
            -Headers @{ Authorization = "Bearer $adminKey" } -UseBasicParsing -TimeoutSec 5).StatusCode
    } catch { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 } }
    if ($status -ne 200) {
        if ($status -notin 401, 501) {
            # A 5xx, a refused connection (0) or a timeout is a broken API, not an unpublished feature: no fallback.
            throw "the authentication probe got HTTP $status (0 means no answer); only 401 or 501 count as 'auth not published', so -AllowAuthDisabled does not apply."
        }
        if (-not $AllowAuthDisabled) {
            throw "authentication does not work: the API answered an admin key with HTTP $status instead of 200. No suite was run. Pass -AllowAuthDisabled only while auth is not published on this branch."
        }
        Write-Host "note: the API answered an admin key with HTTP $status; -AllowAuthDisabled restarts it with authentication disabled (scoped-key scenario will not execute)"
        Stop-Api
        $authMode = 'disabled'
        $baseUrl = Start-Api $false
    }

    @(
        "NACHOS_BASE_URL=$baseUrl"
        "NACHOS_AUTH_MODE=$authMode"
        "NACHOS_ADMIN_KEY=$adminKey"
        "NACHOS_PEER_KEY=$peerKey"
        'NACHOS_AUTH_WORKSPACE=conformance-auth'
        'NACHOS_AUTH_PEER=member'
    ) | Set-Content -Path $EnvFile -Encoding ascii
    # The file holds the keys: inheritance is removed and the current user gets read/write (SYSTEM and Administrators keep
    # their access too), the closest Windows analogue of the mode 600 the bash twin sets.
    if ($IsWindows) {
        icacls $EnvFile /inheritance:r /grant:r "${env:USERNAME}:(R,W)" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "restricting the permissions of $EnvFile failed" }
    } else {
        chmod 600 $EnvFile
    }
    Write-Host "Nachos is ready at $baseUrl (auth: $authMode); environment file: $EnvFile"

    if (-not $Run) {
        Write-Host 'Press Ctrl+C to stop the API.'
        $apiProcess.WaitForExit()
        return
    }

    $suiteSettings = @{}
    foreach ($line in Get-Content $EnvFile) {
        $name, $value = $line -split '=', 2
        $suiteSettings[$name] = $value
    }

    # Both suites always run, so one language's failure does not hide the other's.
    $failed = $false
    $pythonDir = Join-Path $repoRoot 'test/conformance/python'
    $typescriptDir = Join-Path $repoRoot 'test/conformance/typescript'

    Write-Host '== Python (honcho-ai) =='
    $python = if ($IsWindows) { Join-Path $workDir 'venv/Scripts/python.exe' } else { Join-Path $workDir 'venv/bin/python' }
    try {
        $systemPython = (Get-Command python, python3 -ErrorAction SilentlyContinue | Select-Object -First 1).Source
        Invoke-Checked 'creating the venv' { & $systemPython -m venv (Join-Path $workDir 'venv') }
        Invoke-Checked 'installing the Python suite' { & $python -m pip install -q --disable-pip-version-check --require-hashes -r (Join-Path $pythonDir 'requirements.lock') }
        Push-Location $pythonDir
        Invoke-WithEnvironment $suiteSettings { Invoke-Checked 'the Python suite' { & $python -m pytest -rs } }
    } catch { Write-Host "error: $_"; $failed = $true } finally { Pop-Location -ErrorAction SilentlyContinue }

    Write-Host '== TypeScript (@honcho-ai/sdk) =='
    try {
        Push-Location $typescriptDir
        Invoke-Checked 'installing the TypeScript suite' { npm ci --ignore-scripts --no-audit --no-fund --silent }
        Invoke-WithEnvironment $suiteSettings { Invoke-Checked 'the TypeScript suite' { node --test --test-reporter=spec } }
    } catch { Write-Host "error: $_"; $failed = $true } finally { Pop-Location -ErrorAction SilentlyContinue }

    if ($failed) { exit 1 }
}
finally {
    Stop-Api
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}
