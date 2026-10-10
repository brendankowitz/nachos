$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'TestResults.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$log = Join-Path $root 'artifacts/ci/sdk-smoke.log'
if (Test-Path $log) { throw 'Refusing stale SDK results.' }

# Salsa owns the launcher, suites and locks. No open-server fallback or xfail
# admission is supplied here. Exact 5377afc rejects this required flag (exit 2).
& bash (Join-Path $root 'eng/conformance/start-nachos-for-conformance.sh') --run --require-auth 2>&1 |
    Tee-Object -FilePath $log
$nativeExit = $LASTEXITCODE
if ($nativeExit -ne 0) { throw "Authenticated SDK launcher failed with exit $nativeExit. Salsa strict launcher and API auth/token fixes are prerequisites." }
$output = Get-Content $log -Raw
Assert-CiSdkResult $output
if (Select-String -Path (Join-Path $root 'test/conformance/python/test_smoke.py') -Pattern '@pytest\.mark\.xfail' -Quiet) {
    throw 'Salsa must remove the empty-list xfail after the API fix; CI cannot admit it.'
}
