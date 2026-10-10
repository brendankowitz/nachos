<#
.SYNOPSIS
    Downloads a public OpenAPI document, verifies its pinned SHA-256, and writes a manifest of
    interface facts only (routes, query parameter names, status codes, schema fields and bounds).

.DESCRIPTION
    Clean-room guard: the manifest never carries descriptions, titles, examples, summaries or any
    other prose from the source document, and the raw document is never written to disk.
    Output is deterministic: ordinal-sorted keys, 2-space indentation, UTF-8 without BOM, LF newlines.

.PARAMETER Url
    Location of the OpenAPI JSON document.

.PARAMETER ExpectedSha256
    Lowercase hex SHA-256 of the raw response bytes. A mismatch aborts with exit code 1.

.PARAMETER Out
    Path of the manifest to write.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Url,
    [Parameter(Mandatory)] [string] $ExpectedSha256,
    [Parameter(Mandatory)] [string] $Out
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- download raw bytes and verify the pin -----------------------------------------------------

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::All
$client = [System.Net.Http.HttpClient]::new($handler)
try {
    [byte[]] $bytes = $client.GetByteArrayAsync($Url).GetAwaiter().GetResult()
}
finally {
    $client.Dispose()
}

$actual = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
    [Console]::Error.WriteLine("SHA-256 mismatch for ${Url}: expected $($ExpectedSha256.ToLowerInvariant()) but got $actual")
    exit 1
}

$doc = [System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable -Depth 100

# --- helpers -------------------------------------------------------------------------------------

function Get-Facet($Table, [string] $Key) {
    if ($Table -is [System.Collections.IDictionary] -and $Table.Contains($Key)) { return $Table[$Key] }
    return $null
}

function Sort-Ordinal([object[]] $Values) {
    $copy = [string[]] @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [string] $_ })
    [Array]::Sort($copy, [StringComparer]::Ordinal)
    return , $copy
}

function Get-RefName($Table) {
    $ref = Get-Facet $Table '$ref'
    if ($ref) { return ([string] $ref).Substring(([string] $ref).LastIndexOf('/') + 1) }
    return $null
}

# Reduces one property schema to type / nullable / min / max / pattern / enum / ref.
# A union (anyOf) is flattened: 'null' members set nullable, remaining member types are joined with '|'.
function Get-PropertyFacts($Property) {
    $variants = @($Property)
    $nullable = $false
    $anyOf = Get-Facet $Property 'anyOf'
    if ($anyOf) {
        foreach ($member in $anyOf) {
            if ((Get-Facet $member 'type') -eq 'null') { $nullable = $true } else { $variants += $member }
        }
    }

    $types = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $facts = [ordered] @{}
    foreach ($variant in $variants) {
        $type = Get-Facet $variant 'type'
        if ($type -and $type -ne 'null') { [void] $types.Add([string] $type) }

        foreach ($pair in @(
                @('min', 'minLength'), @('min', 'minimum'), @('min', 'minItems'),
                @('max', 'maxLength'), @('max', 'maximum'), @('max', 'maxItems'),
                @('pattern', 'pattern'), @('enum', 'enum'))) {
            $value = Get-Facet $variant $pair[1]
            if ($null -ne $value -and -not $facts.Contains($pair[0])) { $facts[$pair[0]] = $value }
        }

        if (-not $facts.Contains('ref')) {
            $ref = Get-RefName $variant
            if (-not $ref) { $ref = Get-RefName (Get-Facet $variant 'items') }
            if (-not $ref) { $ref = Get-RefName (Get-Facet $variant 'additionalProperties') }
            if ($ref) { $facts['ref'] = $ref }
        }
    }

    $result = [ordered] @{}
    if ($types.Count -gt 0) { $result['type'] = $types -join '|' }
    if ($nullable) { $result['nullable'] = $true }
    foreach ($key in 'min', 'max', 'pattern', 'enum', 'ref') {
        if ($facts.Contains($key)) { $result[$key] = $facts[$key] }
    }
    return $result
}

# --- build the manifest --------------------------------------------------------------------------

$httpMethods = 'delete', 'get', 'head', 'options', 'patch', 'post', 'put', 'trace'
$components = Get-Facet $doc 'components'

function Resolve-Parameter($Parameter) {
    if (Get-Facet $Parameter '$ref') {
        return Get-Facet (Get-Facet $components 'parameters') (Get-RefName $Parameter)
    }
    return $Parameter
}

$routes = foreach ($path in (Sort-Ordinal @($doc['paths'].Keys))) {
    $pathItem = $doc['paths'][$path]
    $shared = @(Get-Facet $pathItem 'parameters')
    foreach ($method in (Sort-Ordinal @($pathItem.Keys | Where-Object { $_ -in $httpMethods }))) {
        $operation = $pathItem[$method]
        $queryNames = @(($shared + @(Get-Facet $operation 'parameters')) |
                Where-Object { $_ } |
                ForEach-Object { Resolve-Parameter $_ } |
                Where-Object { (Get-Facet $_ 'in') -eq 'query' } |
                ForEach-Object { [string] $_['name'] })
        $statuses = @((Get-Facet $operation 'responses').Keys |
                Where-Object { $_ -match '^\d+$' } |
                ForEach-Object { [int] $_ } |
                Sort-Object)
        [ordered] @{
            method   = $method.ToUpperInvariant()
            path     = $path
            query    = Sort-Ordinal $queryNames
            statuses = $statuses
        }
    }
}

$schemas = [ordered] @{}
foreach ($name in (Sort-Ordinal @($components['schemas'].Keys))) {
    $schema = $components['schemas'][$name]
    $properties = [ordered] @{}
    $propertyTable = Get-Facet $schema 'properties'
    if ($propertyTable) {
        foreach ($propertyName in (Sort-Ordinal @($propertyTable.Keys))) {
            $properties[$propertyName] = Get-PropertyFacts $propertyTable[$propertyName]
        }
    }

    $entry = [ordered] @{ required = Sort-Ordinal @(Get-Facet $schema 'required') }
    $entry['properties'] = $properties
    if ($null -ne (Get-Facet $schema 'enum')) {
        $entry['type'] = [string] (Get-Facet $schema 'type')
        $entry['enum'] = Get-Facet $schema 'enum'
    }
    $schemas[$name] = $entry
}

$manifest = [ordered] @{
    source  = $Url
    sha256  = $actual
    version = [string] $doc['info']['version']
    routes  = @($routes)
    schemas = $schemas
}

# --- deterministic JSON writer (2-space indent, insertion-ordered, LF) ---------------------------

$scalarOptions = [System.Text.Json.JsonSerializerOptions]::new()
$scalarOptions.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping

function ConvertTo-ScalarJson($Value) {
    if ($null -eq $Value) { return 'null' }
    return [System.Text.Json.JsonSerializer]::Serialize($Value, $Value.GetType(), $scalarOptions)
}

function Write-Json($Value, [int] $Level, [System.Text.StringBuilder] $Builder) {
    $indent = '  ' * ($Level + 1)
    $closing = '  ' * $Level
    if ($Value -is [System.Collections.IDictionary]) {
        if ($Value.Count -eq 0) { [void] $Builder.Append('{}'); return }
        [void] $Builder.Append("{`n")
        $first = $true
        foreach ($key in $Value.Keys) {
            if (-not $first) { [void] $Builder.Append(",`n") }
            $first = $false
            [void] $Builder.Append($indent).Append((ConvertTo-ScalarJson ([string] $key))).Append(': ')
            Write-Json $Value[$key] ($Level + 1) $Builder
        }
        [void] $Builder.Append("`n").Append($closing).Append('}')
    }
    elseif ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = @($Value)
        if ($items.Count -eq 0) { [void] $Builder.Append('[]'); return }
        [void] $Builder.Append("[`n")
        $first = $true
        foreach ($item in $items) {
            if (-not $first) { [void] $Builder.Append(",`n") }
            $first = $false
            [void] $Builder.Append($indent)
            Write-Json $item ($Level + 1) $Builder
        }
        [void] $Builder.Append("`n").Append($closing).Append(']')
    }
    else {
        [void] $Builder.Append((ConvertTo-ScalarJson $Value))
    }
}

$builder = [System.Text.StringBuilder]::new()
Write-Json $manifest 0 $builder
[void] $builder.Append("`n")

$outPath = [System.IO.Path]::GetFullPath($Out, (Get-Location).ProviderPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outPath)) | Out-Null
[System.IO.File]::WriteAllText($outPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))

Write-Host "Wrote $Out (version $($manifest.version), $(@($routes).Count) routes, $($schemas.Count) schemas)."
