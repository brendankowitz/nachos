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

# Poll until the status is 200 and the body is exactly "Healthy" (same budget as postdeploy.sh). azd waits for the
# ARM operation, not for the traffic switch, so the first answers can still come from the placeholder revision (or a
# warming API: Degraded, or 503 Unhealthy) and are retried. A non-2xx answer surfaces as HttpResponseException; its
# body is kept so the failure report shows what the API last said. -TimeoutSec bounds each attempt.
$attempts = 10
$delaySeconds = 6
$body = ''
$status = 0
for ($attempt = 1; $attempt -le $attempts; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 30
        $status = [int]$response.StatusCode
        $body = [string]$response.Content
    }
    catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        $status = [int]$_.Exception.Response.StatusCode
        $body = [string]$_.ErrorDetails.Message
    }
    catch {
        $status = 0
        $body = ''
    }
    if ($status -eq 200 -and $body.Trim() -ceq 'Healthy') {
        Write-Output "postdeploy: $env:NACHOS_API_URI is ready (the deployed API reports Healthy)."
        return
    }
    if ($attempt -lt $attempts) {
        Start-Sleep -Seconds $delaySeconds
    }
}

# Show at most 200 printable characters of the last answer; never the raw body.
$shown = ($body.Substring(0, [Math]::Min(200, $body.Length)) -replace '[^\x20-\x7E]', '')
throw "postdeploy: after $attempts attempts /health/ready last answered '$shown'. $placeholderStillServing"
