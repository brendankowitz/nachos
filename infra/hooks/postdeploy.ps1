# azd postdeploy hook (Windows): smoke-test the freshly deployed API. Only a GET; touches no secrets.
# The real API's /health/ready answers with the default ASP.NET Core writer's plain-text "Healthy"; the
# placeholder echo server answers 200 with a JSON dump of the request, so only the exact body proves that the
# deployed image (not the placeholder) is serving.
# If postprovision succeeded but `azd deploy` failed or was skipped, the public placeholder stays live
# (it echoes request headers); redeploy with `azd deploy`, or `azd down` if that is not possible.
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:NACHOS_API_URI)) {
    throw 'postdeploy: NACHOS_API_URI is not set.'
}

$uri = $env:NACHOS_API_URI.TrimEnd('/') + '/health/ready'
$placeholderStillServing = 'The placeholder image is still serving (or the API is not healthy). Re-run `azd deploy`; if that cannot succeed, run `azd down`.'

# The new revision can take a moment to become ready, hence the retries (same budget as postdeploy.sh).
$attempts = 10
$body = $null
for ($attempt = 1; $attempt -le $attempts; $attempt++) {
    try {
        $body = [string](Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 30).Content
        break
    }
    catch {
        if ($attempt -eq $attempts) {
            throw "postdeploy: $uri did not answer after $attempts attempts. $placeholderStillServing"
        }
        Start-Sleep -Seconds 6
    }
}

if ($body.Trim() -cne 'Healthy') {
    # Show at most 200 printable characters of what answered; never the raw body.
    $shown = ($body.Substring(0, [Math]::Min(200, $body.Length)) -replace '[^\x20-\x7E]', '')
    throw "postdeploy: /health/ready answered '$shown'. $placeholderStillServing"
}
Write-Output "postdeploy: $env:NACHOS_API_URI is ready (the deployed API reports Healthy)."
