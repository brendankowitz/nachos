function Assert-CiTestResult {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Suite,
        [Parameter(Mandatory)] [int] $NativeExitCode
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Suite produced no fresh TRX (dotnet exit $NativeExitCode)."
    }

    [xml] $trx = Get-Content -LiteralPath $Path -Raw
    $counters = $trx.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    if (-not $counters) { throw "$Suite TRX has no counters." }
    $results = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $passed = @($results | Where-Object outcome -eq 'Passed').Count
    $failed = @($results | Where-Object outcome -eq 'Failed').Count
    $skipped = @($results | Where-Object outcome -eq 'NotExecuted')
    $summary = "$Suite : total=$($counters.total), executed=$($counters.executed), passed=$passed, failed=$failed, skipped=$($skipped.Count), dotnet-exit=$NativeExitCode"
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "- $summary" }
    if ([int] $counters.total -ne $results.Count -or [int] $counters.passed -ne $passed -or
        [int] $counters.failed -ne $failed -or [int] $counters.executed -ne ($passed + $failed) -or
        ($passed + $failed) -eq 0 -or ($passed + $failed + $skipped.Count) -ne $results.Count) {
        throw "$Suite has zero executed tests, inconsistent counters, or an unexpected outcome."
    }
    foreach ($skip in $skipped) {
        $message = $skip.SelectSingleNode('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]')
        $reason = if ($message) { $message.InnerText.Trim() } else { '' }
        Write-Host "NOT EXECUTED: $($skip.testName): $reason"
        $allowed = (
            $Suite -eq 'Nachos.LicenseCheck.Tests' -and (
                ($IsWindows -and $reason -eq 'Not executed: Linux-only native open/statx evidence path.') -or
                ($IsLinux -and $reason -eq "Not executed: creating device nodes (mknod, CAP_MKNOD) in the test's own directory requires NACHOS_LINUX_DEVICE_FIXTURES=1.")
            )
        ) -or (
            $Suite -eq 'Nachos.Infra.Tests' -and $IsWindows -and
            $reason -eq 'Needs a POSIX /usr/bin:/bin environment; covered in Linux CI.'
        )
        if (-not $allowed) { throw "$Suite contains an unapproved skip: $($skip.testName): $reason" }
    }
    if ($NativeExitCode -ne 0 -or $failed -ne 0) { throw "$Suite failed (dotnet exit $NativeExitCode; failed=$failed)." }
}

function Assert-CiSdkResult {
    param([AllowEmptyString()] [string] $Output)
    $python = [regex]::Matches($Output, '(?m)^=+\s+(\d+) passed(?:, [^\r\n]*warnings?)? in [^\r\n]+=+\s*$')
    $typescript = [regex]::Matches($Output, '(?m)^.* tests (\d+)\r?$')
    $passed = [regex]::Matches($Output, '(?m)^.* pass (\d+)\r?$')
    # Current unfiltered suites contain ten Python and nine TypeScript cases,
    # including each language's scoped-key success/denial case. These are minima.
    if ($python.Count -ne 1 -or [int] $python[0].Groups[1].Value -lt 10 -or
        $typescript.Count -ne 1 -or [int] $typescript[0].Groups[1].Value -lt 9 -or
        $passed.Count -ne 1 -or $passed[0].Groups[1].Value -ne $typescript[0].Groups[1].Value) {
        throw 'Both complete SDK suites must report actual passing test counts (Python >=10; TypeScript >=9).'
    }
    foreach ($counter in @('fail', 'skipped', 'todo')) {
        $values = [regex]::Matches($Output, "(?m)^.* $counter (\d+)\r?$")
        if ($values.Count -ne 1 -or [int] $values[0].Groups[1].Value -ne 0) {
            throw "SDK $counter count is missing or nonzero."
        }
    }
    if ($Output -match '(?i)\b\d+ (skipped|xfailed|xpassed|deselected)\b|auth: disabled') {
        throw 'SDK smoke contains a skip, xfail, or disabled authentication.'
    }
    Write-Host "Authenticated SDK smoke: Python=$($python[0].Groups[1].Value), TypeScript=$($typescript[0].Groups[1].Value); no skipped/xfail scenarios."
}
