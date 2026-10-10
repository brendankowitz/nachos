param(
    [Parameter(Mandatory)] [string] $YamlAssembly,
    [string] $SourceRoot = (Join-Path $PSScriptRoot '../..')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path $YamlAssembly
function Child {
    param($Node, [string] $Key)
    $keyNode = [YamlDotNet.RepresentationModel.YamlScalarNode]::new($Key)
    if (-not $Node.Children.ContainsKey($keyNode)) { throw "Missing YAML key: $Key" }
    return ,$Node.Children[$keyNode]
}
function Assert-Policy {
    param($Document)
    $events = Child $Document 'on'
    if (($events.Children.Keys.Value | Sort-Object) -join ',' -ne 'pull_request,push,workflow_dispatch') {
        throw 'Workflow event set changed.'
    }
    $push = Child $events 'push'
    if ($push -isnot [YamlDotNet.RepresentationModel.YamlMappingNode]) { throw 'Push remains unrestricted.' }
    $branches = Child $push 'branches'
    if ($branches -isnot [YamlDotNet.RepresentationModel.YamlSequenceNode] -or
        $branches.Children.Count -ne 1 -or $branches.Children[0].Value -ne 'main' -or
        $push.Children.Count -ne 1) { throw 'Push must target main only.' }
    $concurrency = Child $Document 'concurrency'
    $group = (Child $concurrency 'group').Value
    $cancel = (Child $concurrency 'cancel-in-progress').Value
    if ($group -cne '${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}' -or
        $cancel -cne '${{ github.event_name == ''pull_request'' }}') { throw 'Unsupported concurrency policy expression.' }
    $name = (Child $Document 'name').Value
    $cases = @(
        @{ Event='push'; Ref='refs/heads/topic'; Pr=0; Run=$false; Cancel=$false; Group="$name-refs/heads/topic" },
        @{ Event='pull_request'; Ref='refs/pull/42/merge'; Pr=42; Run=$true; Cancel=$true; Group="$name-42" },
        @{ Event='pull_request'; Ref='refs/pull/42/head'; Pr=42; Run=$true; Cancel=$true; Group="$name-42" },
        @{ Event='pull_request'; Ref='refs/pull/43/merge'; Pr=43; Run=$true; Cancel=$true; Group="$name-43" },
        @{ Event='push'; Ref='refs/heads/main'; Pr=0; Run=$true; Cancel=$false; Group="$name-refs/heads/main" },
        @{ Event='workflow_dispatch'; Ref='refs/heads/topic'; Pr=0; Run=$true; Cancel=$false; Group="$name-refs/heads/topic" }
    )
    foreach ($case in $cases) {
        $runs = $case.Event -ne 'push' -or $case.Ref -eq "refs/heads/$($branches.Children[0].Value)"
        $key = if ($case.Pr) { [string] $case.Pr } else { $case.Ref }
        $computedGroup = $group.Replace('${{ github.workflow }}', $name).Replace('${{ github.event.pull_request.number || github.ref }}', $key)
        $computedCancel = $case.Event -eq 'pull_request'
        if ($runs -ne $case.Run -or $computedCancel -ne $case.Cancel -or $computedGroup -cne $case.Group) {
            throw "Event policy mismatch: $($case.Event) $($case.Ref)"
        }
    }
    return $cases.Count
}
$total = 0
$mutants = 0
foreach ($file in @('ci.yml', 'docs-validate.yml')) {
    $reader = [IO.StringReader]::new((Get-Content (Join-Path $SourceRoot ".github/workflows/$file") -Raw))
    $yaml = [YamlDotNet.RepresentationModel.YamlStream]::new()
    try { $yaml.Load($reader) } finally { $reader.Dispose() }
    if ($yaml.Documents.Count -ne 1) { throw 'Expected one YAML document.' }
    $root = $yaml.Documents[0].RootNode
    $total += Assert-Policy $root
    Write-Host "PASS ${file}: parsed main/PR/manual event and concurrency scenarios"
    $branchNode = (Child (Child (Child $root 'on') 'push') 'branches').Children[0]
    $cancelNode = Child (Child $root 'concurrency') 'cancel-in-progress'
    $groupNode = Child (Child $root 'concurrency') 'group'
    foreach ($mutation in @(
        @{ Node=$branchNode; Value='topic'; Name='non-main push' },
        @{ Node=$cancelNode; Value='true'; Name='main cancellation' },
        @{ Node=$groupNode; Value='${{ github.workflow }}-${{ github.ref }}'; Name='PR identity loss' }
    )) {
        $saved = $mutation.Node.Value
        $rejected = $false
        try {
            $mutation.Node.Value = $mutation.Value
            try { Assert-Policy $root | Out-Null } catch { $rejected = $true }
        }
        finally { $mutation.Node.Value = $saved }
        if (-not $rejected) { throw "Policy guard missed $($mutation.Name)." }
        $mutants++
    }
}
Write-Host "Parsed workflow policy scenarios: $total passed (local model, not hosted event execution)."
Write-Host "Parsed-policy mutations rejected and restored: $mutants."
