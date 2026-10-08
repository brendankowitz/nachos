# Maintainer-only data refresh. Never invoked by the audit or CI.
$ErrorActionPreference = 'Stop'
$release = 'v3.27.0'
$ids = @('MIT', 'MIT-0', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', '0BSD', 'ISC',
    'MS-PL', 'Unlicense', 'CC0-1.0', 'BlueOak-1.0.0', 'Zlib', 'PSF-2.0', 'Python-2.0', 'EPL-2.0', 'MPL-2.0')
$texts = [ordered]@{}
foreach ($id in $ids) {
    $uri = "https://raw.githubusercontent.com/spdx/license-list-data/$release/json/details/$id.json"
    $data = Invoke-RestMethod -Uri $uri
    if ($data.licenseId -cne $id -or [string]::IsNullOrWhiteSpace($data.licenseText)) {
        throw "Invalid canonical license document: $uri"
    }
    $texts[$id] = $data.licenseText
}
$json = $texts | ConvertTo-Json -Depth 3
$json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'license-templates.json') -Encoding utf8NoBOM
$fixtures = Join-Path $PSScriptRoot '../../test/Nachos.LicenseCheck.Tests/Fixtures'
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
$json | Set-Content -LiteralPath (Join-Path $fixtures 'complete-licenses.json') -Encoding utf8NoBOM
