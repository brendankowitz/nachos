$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$hadSummary = Test-Path Env:GITHUB_STEP_SUMMARY
$savedSummary = $env:GITHUB_STEP_SUMMARY
$directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../artifacts/ci/summary-tests/$([guid]::NewGuid().ToString('N'))"))
$checks = 0
function Assert-SummaryState {
    param([bool] $Present, [AllowNull()] [AllowEmptyString()] [string] $Value)
    if ((Test-Path Env:GITHUB_STEP_SUMMARY) -ne $Present -or ($Present -and $env:GITHUB_STEP_SUMMARY -cne $Value)) {
        throw 'The caller summary environment presence/value was not restored exactly.'
    }
}
try {
    New-Item -ItemType Directory -Path $directory | Out-Null
    $sentinel = Join-Path $directory 'caller-summary.txt'
    [IO.File]::WriteAllText($sentinel, "caller summary: untouched`n", [Text.UTF8Encoding]::new($false))
    $before = (Get-FileHash $sentinel).Hash
    $env:GITHUB_STEP_SUMMARY = $sentinel
    & (Join-Path $PSScriptRoot 'Test-CiHelpers.ps1')
    Assert-SummaryState $true $sentinel
    if ((Get-FileHash $sentinel).Hash -ne $before) { throw 'Self-tests appended synthetic results to the caller summary.' }
    Write-Host 'PASS self-tests preserve caller summary bytes and environment'
    $checks++

    # Missing the companion import forces a real setup failure before fixtures run.
    $failingEntry = Join-Path $directory 'Test-CiHelpers.ps1'
    Copy-Item (Join-Path $PSScriptRoot 'Test-CiHelpers.ps1') $failingEntry
    foreach ($state in @('present', 'absent', 'empty')) {
        if ($state -eq 'absent') { Remove-Item Env:GITHUB_STEP_SUMMARY }
        elseif ($state -eq 'empty') { $env:GITHUB_STEP_SUMMARY = '' }
        else { $env:GITHUB_STEP_SUMMARY = $sentinel }
        $present = Test-Path Env:GITHUB_STEP_SUMMARY
        $value = $env:GITHUB_STEP_SUMMARY
        $failed = $false
        try { & $failingEntry } catch { $failed = $true }
        if (-not $failed) { throw 'The missing-import failure fixture unexpectedly succeeded.' }
        Assert-SummaryState $present $value
        if ((Get-FileHash $sentinel).Hash -ne $before) { throw 'Failure cleanup changed the caller summary.' }
        Write-Host "PASS failure cleanup restores $state summary state"
        $checks++
    }

    . (Join-Path $PSScriptRoot 'TestResults.ps1')
    $env:GITHUB_STEP_SUMMARY = $sentinel
    $trx = Join-Path $directory 'summary-contract.trx'
    Set-Content $trx '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="summary-writer" outcome="Passed" /></Results><ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary></TestRun>'
    Assert-CiTestResult $trx 'summary-writer-contract' 0
    if (-not (Select-String -LiteralPath $sentinel -SimpleMatch -Pattern 'summary-writer-contract : total=1, executed=1, passed=1' -Quiet)) {
        throw 'Production test-result summary writing was disabled.'
    }
    Write-Host 'PASS production summary writer remains enabled (private fixture only)'
    $checks++
    Write-Host "Summary boundary checks: $checks passed."
}
finally {
    if ($hadSummary) { $env:GITHUB_STEP_SUMMARY = $savedSummary }
    else { Remove-Item Env:GITHUB_STEP_SUMMARY -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
