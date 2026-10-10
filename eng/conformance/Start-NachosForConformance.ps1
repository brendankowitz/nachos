# UNVERIFIED on this host: pwsh is not installed here, so this script has never been run. The tested implementation is
# start-nachos-for-conformance.sh; this is a deliberately thin twin with the same flags and behaviour (see that file's
# header for the auth probe and the environment file contents).
#
# Usage: Start-NachosForConformance.ps1 [-Run] [-EnvFile <path>] [-TimeoutSeconds <n>]
[CmdletBinding()]
param(
    [switch]$Run,
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

# Starts the API on a kernel-chosen port and waits for /health/ready. Returns the base URL.
function Start-Api([bool]$authEnabled) {
    $log = Join-Path $workDir 'api.log'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:0'
    $env:Nachos__Auth__Enabled = "$authEnabled".ToLowerInvariant()
    $env:Nachos__Auth__NachosKey__Keys__0__Kid = 'dev'
    $env:Nachos__Auth__NachosKey__Keys__0__Secret = $env:NACHOS_CONFORMANCE_SECRET
    $script:apiProcess = Start-Process dotnet -ArgumentList "`"$apiDll`"" -WorkingDirectory (Split-Path $apiDll) `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $baseUrl = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:apiProcess.HasExited) { throw 'the API exited during startup' }
        if (-not $baseUrl -and (Test-Path $log)) {
            $match = Select-String -Path $log -Pattern 'http://127\.0\.0\.1:\d+' | Select-Object -First 1
            if ($match) { $baseUrl = $match.Matches[0].Value }
        }
        if ($baseUrl) {
            try {
                Invoke-WebRequest "$baseUrl/health/ready" -UseBasicParsing -TimeoutSec 2 | Out-Null
                return $baseUrl
            } catch { }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "the API was not ready within $TimeoutSeconds s"
}

try {
    Invoke-Checked 'building Nachos.Api' { dotnet build (Join-Path $repoRoot 'src/Nachos.Api/Nachos.Api.csproj') -c Release -v q --nologo }
    Invoke-Checked 'building Nachos.Cli' { dotnet build (Join-Path $repoRoot 'src/Nachos.Cli/Nachos.Cli.csproj') -c Release -v q --nologo }
    $apiDll = Get-TargetPath 'Nachos.Api'
    $cliDll = Get-TargetPath 'Nachos.Cli'

    # The signing secret lives only in this process's environment and the API's; it is never printed or written.
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $env:NACHOS_CONFORMANCE_SECRET = [Convert]::ToBase64String($bytes)

    $adminKey = (& dotnet $cliDll keys create --signing-secret-env NACHOS_CONFORMANCE_SECRET --kid dev --admin).Trim()
    $peerKey = (& dotnet $cliDll keys create --signing-secret-env NACHOS_CONFORMANCE_SECRET --kid dev --workspace conformance-auth --peer member).Trim()

    $authMode = 'enforced'
    $baseUrl = Start-Api $true
    $status = try {
        (Invoke-WebRequest "$baseUrl/v3/workspaces/list" -Method Post -Body '{}' -ContentType 'application/json' `
            -Headers @{ Authorization = "Bearer $adminKey" } -UseBasicParsing -TimeoutSec 5).StatusCode
    } catch { [int]$_.Exception.Response.StatusCode }
    if ($status -ne 200) {
        Write-Host 'note: the API refused an admin key; restarting with authentication disabled (scoped-key scenario will not execute)'
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

    foreach ($line in Get-Content $EnvFile) {
        $name, $value = $line -split '=', 2
        Set-Item "env:$name" $value
    }

    # Both suites always run, so one language's failure does not hide the other's.
    $failed = $false
    $pythonDir = Join-Path $repoRoot 'test/conformance/python'
    $typescriptDir = Join-Path $repoRoot 'test/conformance/typescript'

    Write-Host '== Python (honcho-ai) =='
    $python = if ($IsWindows) { Join-Path $workDir 'venv/Scripts/python.exe' } else { Join-Path $workDir 'venv/bin/python' }
    try {
        Invoke-Checked 'creating the venv' { python -m venv (Join-Path $workDir 'venv') }
        Invoke-Checked 'installing the Python suite' { & $python -m pip install -q --disable-pip-version-check --require-hashes -r (Join-Path $pythonDir 'requirements.lock') }
        Push-Location $pythonDir
        Invoke-Checked 'the Python suite' { & $python -m pytest -rs }
    } catch { Write-Host "error: $_"; $failed = $true } finally { Pop-Location -ErrorAction SilentlyContinue }

    Write-Host '== TypeScript (@honcho-ai/sdk) =='
    try {
        Push-Location $typescriptDir
        Invoke-Checked 'installing the TypeScript suite' { npm ci --ignore-scripts --no-audit --no-fund --silent }
        Invoke-Checked 'the TypeScript suite' { node --test --test-reporter=spec }
    } catch { Write-Host "error: $_"; $failed = $true } finally { Pop-Location -ErrorAction SilentlyContinue }

    if ($failed) { exit 1 }
}
finally {
    Stop-Api
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}
