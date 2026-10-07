# azd postdeploy hook (Windows): smoke-test the freshly deployed API. Only a GET; touches no secrets.
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:NACHOS_API_URI)) {
    throw 'postdeploy: NACHOS_API_URI is not set.'
}

$uri = $env:NACHOS_API_URI.TrimEnd('/') + '/health/ready'

# The new revision can take a moment to become ready, hence the retries (same budget as postdeploy.sh).
$attempts = 10
for ($attempt = 1; $attempt -le $attempts; $attempt++) {
    try {
        $response = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 30
        Write-Output $response
        Write-Output "postdeploy: $env:NACHOS_API_URI is ready."
        return
    }
    catch {
        if ($attempt -eq $attempts) {
            throw "postdeploy: $uri did not become ready after $attempts attempts. $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 6
    }
}
