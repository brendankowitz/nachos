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
function HasKey {
    param($Node, [string] $Key)
    return $Node.Children.ContainsKey([YamlDotNet.RepresentationModel.YamlScalarNode]::new($Key))
}
function Scalar {
    param($Node, [string] $Key)
    return (Child $Node $Key).Value
}
function Assert-TopPermissions {
    param($Document, [string] $File)
    $permissions = Child $Document 'permissions'
    if ($permissions.Children.Count -ne 1 -or (Scalar $permissions 'contents') -ne 'read') {
        throw "$File workflow permissions must be exactly contents: read."
    }
}
function Assert-MainOnlyExpression {
    param([string] $Value, [string] $Context)
    $expected = '${{ (github.event_name == ''push'' || github.event_name == ''workflow_dispatch'') && github.ref == ''refs/heads/main'' }}'
    if ($Value -cne $expected) { throw "$Context must be main-only push/workflow_dispatch." }
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
foreach ($file in @('ci.yml', 'docs-validate.yml', 'docs-site.yml')) {
    $reader = [IO.StringReader]::new((Get-Content (Join-Path $SourceRoot ".github/workflows/$file") -Raw))
    $yaml = [YamlDotNet.RepresentationModel.YamlStream]::new()
    try { $yaml.Load($reader) } finally { $reader.Dispose() }
    if ($yaml.Documents.Count -ne 1) { throw 'Expected one YAML document.' }
    $root = $yaml.Documents[0].RootNode
    $total += Assert-Policy $root
    Assert-TopPermissions $root $file
    if ($file -eq 'docs-site.yml') {
        $jobs = Child $root 'jobs'
        foreach ($entry in $jobs.Children.GetEnumerator()) {
            $jobName = $entry.Key.Value
            $job = $entry.Value
            if ($jobName -in @('source', 'licenses')) {
                foreach ($key in @('permissions', 'environment', 'continue-on-error', 'uses')) {
                    if (HasKey $job $key) { throw "Documentation source/audit job cannot set $key." }
                }
            }
            elseif ($jobName -eq 'pages') {
                $permissions = Child $job 'permissions'
                if ($permissions.Children.Count -ne 1 -or (Scalar $permissions 'contents') -ne 'read') {
                    throw 'Pages artifact job permissions must be exactly contents: read.'
                }
                if (HasKey $job 'environment' -or HasKey $job 'continue-on-error' -or HasKey $job 'uses') {
                    throw 'Pages artifact job cannot set environment, continue-on-error or uses.'
                }
                $needs = Child $job 'needs'
                if ($needs.Value -ne 'source') { throw 'Pages artifact job must need source.' }
            }
            elseif ($jobName -eq 'deploy') {
                Assert-MainOnlyExpression (Scalar $job 'if') 'Deploy job'
                $needs = Child $job 'needs'
                if ($needs -isnot [YamlDotNet.RepresentationModel.YamlSequenceNode] -or
                    ($needs.Children.Value | Sort-Object) -join ',' -cne 'pages,source') {
                    throw 'Deploy job must need both source and pages.'
                }
                $permissions = Child $job 'permissions'
                if ($permissions.Children.Count -ne 2 -or (Scalar $permissions 'pages') -ne 'write' -or
                    (Scalar $permissions 'id-token') -ne 'write') {
                    throw 'Deploy job permissions must be exactly pages: write and id-token: write.'
                }
                $environment = Child $job 'environment'
                if ((Scalar $environment 'name') -ne 'github-pages') { throw 'Deploy job must target github-pages environment.' }
                $concurrency = Child $job 'concurrency'
                if ((Scalar $concurrency 'group') -ne 'pages' -or (Scalar $concurrency 'cancel-in-progress') -ne 'false') {
                    throw 'Deploy job must use non-canceling Pages concurrency.'
                }
                $steps = Child $job 'steps'
                if ($steps.Children.Count -ne 1) { throw 'Deploy job must contain exactly one step.' }
                $step = $steps.Children[0]
                if (HasKey $step 'run' -or HasKey $step 'with' -or HasKey $step 'continue-on-error') {
                    throw 'Deploy step cannot run commands, configure inputs or hide failures.'
                }
                if ((Scalar $step 'id') -ne 'deployment') { throw 'Deploy step id must be deployment.' }
                $action = Scalar $step 'uses'
                if ($action -cnotmatch '^actions/deploy-pages@[a-f0-9]{40}$') {
                    throw "Deploy job must use SHA-pinned actions/deploy-pages: $action"
                }
                continue
            }
            else { throw "Unexpected documentation workflow job: $jobName" }
            foreach ($step in (Child $job 'steps').Children) {
                if ($step.Children.ContainsKey([YamlDotNet.RepresentationModel.YamlScalarNode]::new('continue-on-error'))) {
                    throw 'Documentation steps cannot hide failures.'
                }
                if ($step.Children.ContainsKey([YamlDotNet.RepresentationModel.YamlScalarNode]::new('uses'))) {
                    $action = (Child $step 'uses').Value
                    if ($action -cmatch '^actions/upload-pages-artifact@') {
                        if ($jobName -ne 'pages' -or $action -cnotmatch '^actions/upload-pages-artifact@[a-f0-9]{40}$') {
                            throw "Upload Pages artifact is only allowed as a SHA-pinned pages job action: $action"
                        }
                        Assert-MainOnlyExpression (Scalar $step 'if') 'Upload Pages artifact step'
                    }
                    elseif ($action -cnotmatch '^actions/(checkout|setup-dotnet|setup-node|setup-python)@[a-f0-9]{40}$') {
                        throw "Documentation CI only permits SHA-pinned source setup actions: $action"
                    }
                    if ($action.StartsWith('actions/checkout@') -and
                        (Scalar (Child $step 'with') 'persist-credentials') -ne 'false') {
                        throw 'Documentation checkout must not persist credentials.'
                    }
                    if ($action -cmatch '^actions/(upload-artifact|download-artifact|deploy-pages)@') {
                        throw "Unexpected upload/deploy action outside the reviewed Pages profile: $action"
                    }
                }
            }
        }
    }
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
