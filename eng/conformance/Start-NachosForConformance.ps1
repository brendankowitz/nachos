# UNVERIFIED on Windows by its author: the only host available was Linux, where this script was exercised under
# pwsh 7.6.6 (a dotnet tool) with -Run -AllowAuthDisabled. The reference implementation is
# start-nachos-for-conformance.sh; this is a deliberately thin twin with the same flags and behaviour (see that file's
# header for the strict auth probe and the environment file contents). Authentication is strict by default: a refused
# admin probe is an error and no suite runs. -AllowAuthDisabled is the development-only fallback for a branch where auth
# is not published yet.
#
# Hermetic: the API and the CLI key-minting commands get an explicit allow-listed environment built from scratch, so
# an ambient SQL, Key Vault, Entra, Azure or telemetry setting cannot redirect the in-memory, offline run. The caller's
# own environment is never modified for them; the suites' NACHOS_* settings are restored in a finally block.
#
# Usage: Start-NachosForConformance.ps1 [-Run] [-AllowAuthDisabled] [-EnvFile <path>] [-TimeoutSeconds <n>]
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$AllowAuthDisabled,
    [string]$EnvFile,
    [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'

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

function Get-TargetPath([string]$project) {
    (& dotnet msbuild (Join-Path $repoRoot "src/$project/$project.csproj") -p:Configuration=Release -getProperty:TargetPath).Trim()
}

# The names a child process may inherit. Everything else the caller exported is dropped (same list as the bash twin,
# plus the Windows basics .NET needs).
$allowedEnvironment = 'PATH', 'HOME', 'DOTNET_ROOT', 'LANG', 'LC_ALL', 'TMPDIR', 'SystemRoot', 'USERPROFILE', 'TEMP', 'TMP'

# A `dotnet <dll>` start description whose environment is cleared and rebuilt from the allow-list plus $settings.
function New-HermeticStartInfo([string]$dll, [hashtable]$settings) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.ArgumentList.Add($dll)
    $info.WorkingDirectory = Split-Path $dll
    $info.UseShellExecute = $false
    $info.Environment.Clear()
    foreach ($name in $allowedEnvironment) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $info.Environment[$name] = $value }
    }
    $info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $info.Environment['DOTNET_NOLOGO'] = '1'
    foreach ($name in $settings.Keys) { $info.Environment[$name] = $settings[$name] }
    $info
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
    $info = New-HermeticStartInfo $cliDll @{ NACHOS_CONFORMANCE_SECRET = $conformanceSecret }
    foreach ($argument in @('keys', 'create', '--signing-secret-env', 'NACHOS_CONFORMANCE_SECRET', '--kid', 'dev') + $keyArguments) {
        $info.ArgumentList.Add($argument)
    }
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

    $info = New-HermeticStartInfo $apiDll @{
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
    Invoke-Checked 'building Nachos.Api' { dotnet build (Join-Path $repoRoot 'src/Nachos.Api/Nachos.Api.csproj') -c Release -v q --nologo }
    Invoke-Checked 'building Nachos.Cli' { dotnet build (Join-Path $repoRoot 'src/Nachos.Cli/Nachos.Cli.csproj') -c Release -v q --nologo }
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
    if (-not $IsWindows) { chmod 600 $EnvFile }
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
