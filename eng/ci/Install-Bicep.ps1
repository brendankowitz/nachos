$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/ci/tools/bicep'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
if ($IsWindows) {
    $asset = 'bicep-win-x64.exe'
    $name = 'bicep.exe'
    $sha256 = '398af294cf16ac4becdbd008a77d148d0e8e91492309a6b60ec911b7e1cab082'
}
elseif ($IsLinux) {
    $asset = 'bicep-linux-x64'
    $name = 'bicep'
    $sha256 = 'b09ec25a9d376c1f8e33ede6ed22b587f915ad68488d5db77a6f9541748c7f6e'
}
else { throw 'Only the GitHub-hosted x64 Windows/Linux CI targets are supported.' }
$binary = Join-Path $directory $name
if (-not (Test-Path $binary)) {
    Invoke-WebRequest "https://github.com/Azure/bicep/releases/download/v0.48.1/$asset" -OutFile $binary
}
if ((Get-FileHash $binary -Algorithm SHA256).Hash -ne $sha256) { throw 'Pinned compiler v0.48.1 SHA-256 mismatch.' }
if ($IsLinux) {
    & chmod +x $binary
    if ($LASTEXITCODE -ne 0) { throw 'Could not mark the pinned compiler executable.' }
}
$env:PATH = $directory + [IO.Path]::PathSeparator + $env:PATH
& $binary --version
if ($LASTEXITCODE -ne 0) { throw 'The pinned compiler executable did not run.' }
