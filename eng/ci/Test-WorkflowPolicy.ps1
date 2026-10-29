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
function Assert-KeySet {
    param($Node, [string[]] $Expected, [string] $Context)
    $actual = @($Node.Children.Keys.Value | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    if (($actual -join ',') -cne ($expectedSorted -join ',')) {
        throw "$Context keys changed: expected $($expectedSorted -join ',') but found $($actual -join ',')."
    }
}
function GrantsCredential {
    param($Permissions)
    if ($Permissions -is [YamlDotNet.RepresentationModel.YamlScalarNode]) {
        return $Permissions.Value -in @('write-all', 'read-all-with-write')
    }
    if ($Permissions -isnot [YamlDotNet.RepresentationModel.YamlMappingNode]) {
        throw 'Permissions must be a mapping or recognized scalar.'
    }
    foreach ($entry in $Permissions.Children.GetEnumerator()) {
        $key = $entry.Key.Value
        $value = $entry.Value.Value
        if ($key -in @('id-token', 'pages')) { return $true }
        if ($value -eq 'write') { return $true }
        if ($value -in @('write-all', 'read-all-with-write')) { return $true }
    }
    return $false
}
function Assert-TopPermissions {
    param($Document, [string] $File)
    $permissions = Child $Document 'permissions'
    if (GrantsCredential $permissions) {
        throw "$File workflow permissions cannot grant write, Pages or OIDC credentials."
    }
    if ($permissions -isnot [YamlDotNet.RepresentationModel.YamlMappingNode]) {
        throw "$File workflow permissions must be exactly contents: read."
    }
    if ($permissions.Children.Count -ne 1 -or (Scalar $permissions 'contents') -ne 'read') {
        throw "$File workflow permissions must be exactly contents: read."
    }
}
function Assert-MainOnlyExpression {
    param([string] $Value, [string] $Context)
    $expected = '${{ (github.event_name == ''push'' || github.event_name == ''workflow_dispatch'') && github.ref == ''refs/heads/main'' }}'
    if ($Value -cne $expected) { throw "$Context must be main-only push/workflow_dispatch." }
}
function Assert-PagesUploadStep {
    param($Step)
    Assert-KeySet $Step @('uses', 'if', 'with') 'Upload Pages artifact step'
    Assert-MainOnlyExpression (Scalar $Step 'if') 'Upload Pages artifact step'
    $with = Child $Step 'with'
    if ($with -isnot [YamlDotNet.RepresentationModel.YamlMappingNode]) {
        throw 'Upload Pages artifact step inputs must be a mapping.'
    }
    Assert-KeySet $with @('path') 'Upload Pages artifact inputs'
    if ((Scalar $with 'path') -ne 'docs/site/dist') {
        throw 'Upload Pages artifact path must be docs/site/dist.'
    }
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
$workflowDirectory = Join-Path $SourceRoot '.github/workflows'
$workflowFiles = @(Get-ChildItem -Path (Join-Path $workflowDirectory '*') -File -Include *.yml,*.yaml | Sort-Object Name)
foreach ($workflowFile in $workflowFiles) {
    $file = $workflowFile.Name
    $reader = [IO.StringReader]::new((Get-Content -LiteralPath $workflowFile.FullName -Raw))
    $yaml = [YamlDotNet.RepresentationModel.YamlStream]::new()
    try { $yaml.Load($reader) } finally { $reader.Dispose() }
    if ($yaml.Documents.Count -ne 1) { throw 'Expected one YAML document.' }
    $root = $yaml.Documents[0].RootNode
    $total += Assert-Policy $root
    Assert-TopPermissions $root $file
    $allJobs = Child $root 'jobs'
    foreach ($entry in $allJobs.Children.GetEnumerator()) {
        $jobName = $entry.Key.Value
        $job = $entry.Value
        $isReviewedDeploy = $file -eq 'docs-site.yml' -and $jobName -eq 'deploy'
        if ((HasKey $job 'permissions') -and (GrantsCredential (Child $job 'permissions')) -and -not $isReviewedDeploy) {
            throw "$file job $jobName cannot grant write, Pages or OIDC credentials."
        }
        if ($isReviewedDeploy -and ((HasKey $job 'uses') -or (HasKey $job 'container') -or (HasKey $job 'services'))) {
            throw 'Deploy job cannot use reusable workflows, containers or services.'
        }
        $credentialJob = (HasKey $job 'permissions') -and (GrantsCredential (Child $job 'permissions'))
        if ($credentialJob -and ((HasKey $job 'uses') -or (HasKey $job 'container') -or (HasKey $job 'services'))) {
            throw "$file job $jobName credential-bearing job cannot use reusable workflows, containers or services."
        }
    }
    if ($file -eq 'docs-site.yml') {
        $jobs = Child $root 'jobs'
        foreach ($entry in $jobs.Children.GetEnumerator()) {
            $jobName = $entry.Key.Value
            $job = $entry.Value
            $uploadStepCount = 0
            if ($jobName -eq 'source') {
                foreach ($key in @('permissions', 'environment', 'continue-on-error', 'uses')) {
                    if (HasKey $job $key) { throw "Documentation source job cannot set $key." }
                }
            }
            elseif ($jobName -eq 'pages') {
                Assert-KeySet $job @('name', 'runs-on', 'needs', 'permissions', 'timeout-minutes', 'steps') 'Pages artifact job'
                if ((Scalar $job 'runs-on') -ne 'ubuntu-24.04') { throw 'Pages artifact job must run on ubuntu-24.04.' }
                $permissions = Child $job 'permissions'
                if ($permissions.Children.Count -ne 1 -or (Scalar $permissions 'contents') -ne 'read') {
                    throw 'Pages artifact job permissions must be exactly contents: read.'
                }
                if ((HasKey $job 'environment') -or (HasKey $job 'continue-on-error') -or (HasKey $job 'uses')) {
                    throw 'Pages artifact job cannot set environment, continue-on-error or uses.'
                }
                $needs = Child $job 'needs'
                if ($needs.Value -ne 'source') { throw 'Pages artifact job must need source.' }
                # The upload must ship the dist sealed and verified by the single publication run,
                # so the whole step sequence is pinned; nothing may run between verify and upload.
                $pagesSteps = (Child $job 'steps').Children
                $expectedActions = @('actions/checkout@', 'actions/setup-dotnet@', 'actions/setup-node@')
                if ($pagesSteps.Count -ne 5) { throw 'Pages artifact job must have exactly checkout, setup-dotnet, setup-node, publication run and upload steps.' }
                for ($index = 0; $index -lt 3; $index++) {
                    if (-not (HasKey $pagesSteps[$index] 'uses') -or -not (Scalar $pagesSteps[$index] 'uses').StartsWith($expectedActions[$index])) {
                        throw "Pages artifact job step $($index + 1) must be $($expectedActions[$index].TrimEnd('@'))."
                    }
                }
                Assert-KeySet $pagesSteps[3] @('name', 'run') 'Pages publication run step'
                if ((Scalar $pagesSteps[3] 'run').Trim() -cne './eng/ci/Invoke-DocsSite.ps1 -Publication') {
                    throw 'Pages publication step must run exactly ./eng/ci/Invoke-DocsSite.ps1 -Publication.'
                }
                if (-not (HasKey $pagesSteps[4] 'uses') -or -not (Scalar $pagesSteps[4] 'uses').StartsWith('actions/upload-pages-artifact@')) {
                    throw 'Pages artifact upload must be the final step, directly after the publication run.'
                }
            }
            elseif ($jobName -eq 'deploy') {
                Assert-KeySet $job @('name', 'runs-on', 'needs', 'if', 'permissions', 'environment', 'concurrency', 'steps') 'Deploy job'
                if ((Scalar $job 'runs-on') -ne 'ubuntu-24.04') { throw 'Deploy job must run on ubuntu-24.04.' }
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
                if ($environment -isnot [YamlDotNet.RepresentationModel.YamlMappingNode] -or $environment.Children.Count -ne 2) {
                    throw 'Deploy job environment must be a mapping with name and url.'
                }
                Assert-KeySet $environment @('name', 'url') 'Deploy job environment'
                if ((Scalar $environment 'name') -ne 'github-pages') { throw 'Deploy job must target github-pages environment.' }
                if ((Scalar $environment 'url') -ne '${{ steps.deployment.outputs.page_url }}') { throw 'Deploy job environment url must come from the deployment step.' }
                $concurrency = Child $job 'concurrency'
                if ((Scalar $concurrency 'group') -ne 'pages' -or (Scalar $concurrency 'cancel-in-progress') -ne 'false') {
                    throw 'Deploy job must use non-canceling Pages concurrency.'
                }
                $steps = Child $job 'steps'
                if ($steps.Children.Count -ne 1) { throw 'Deploy job must contain exactly one step.' }
                $step = $steps.Children[0]
                Assert-KeySet $step @('id', 'uses') 'Deploy step'
                if ((HasKey $step 'run') -or (HasKey $step 'with') -or (HasKey $step 'continue-on-error')) {
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
                        $uploadStepCount++
                        Assert-PagesUploadStep $step
                    }
                    elseif ($jobName -eq 'pages' -and $action -cmatch '^actions/setup-python@') {
                        throw 'Pages artifact job must not install Python.'
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
            if ($jobName -eq 'pages' -and $uploadStepCount -ne 1) {
                throw 'Pages artifact job must contain exactly one upload-pages-artifact step.'
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
