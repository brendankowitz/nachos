# azd postprovision hook (Windows). Same logic as postprovision.sh; see that file for the rationale.
# Never run by agents or CI: it needs an owner-approved `azd provision` (spec 18.3) and an `az login`
# as the principal that is the SQL Entra admin set by Bicep.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true   # a failing native command (az, sqlcmd, dotnet) throws

$required = @(
    'AZURE_RESOURCE_GROUP', 'AZURE_KEY_VAULT_NAME', 'AZURE_SQL_SERVER_NAME', 'AZURE_SQL_SERVER_FQDN',
    'AZURE_SQL_DATABASE_NAME', 'AZURE_MANAGED_IDENTITY_NAME', 'AZURE_MANAGED_IDENTITY_CLIENT_ID'
)
$missing = $required | Where-Object { [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_)) }
if ($missing) {
    throw "postprovision: missing azd environment values: $($missing -join ', ')"
}

Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..' '..'))

# Owner-only temp directory (0700 on Unix); secrets live here and nowhere else.
$workDir = [System.IO.Directory]::CreateTempSubdirectory('nachos-hook-').FullName
$firewallRule = $null

function Read-FileText([string] $path) { [System.IO.File]::ReadAllText($path) }

try {
    # --- 1. Temporary SQL firewall rule for this machine ---------------------------------------------
    # The server only allows Azure-internal traffic, but this hook runs on a developer machine. That is
    # also why running it is owner-approved: it briefly exposes the SQL endpoint to the caller's public IP.
    $publicIp = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 15).ToString().Trim()
    if ($publicIp -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
        throw "postprovision: could not determine this machine's public IPv4 address."
    }
    $firewallRule = "nachos-hook-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
    az sql server firewall-rule create `
        --resource-group $env:AZURE_RESOURCE_GROUP --server $env:AZURE_SQL_SERVER_NAME `
        --name $firewallRule --start-ip-address $publicIp --end-ip-address $publicIp --output none
    Write-Output "postprovision: added temporary firewall rule '$firewallRule'."

    $sqlcmdArgs = @('-S', $env:AZURE_SQL_SERVER_FQDN, '-d', $env:AZURE_SQL_DATABASE_NAME,
        '--authentication-method', 'ActiveDirectoryDefault', '-b')

    # New firewall rules can take a while to take effect; probe until the database answers.
    $connected = $false
    for ($i = 0; $i -lt 10 -and -not $connected; $i++) {
        try {
            sqlcmd @sqlcmdArgs -Q 'SELECT 1' *> $null
            $connected = $true
        }
        catch { Start-Sleep -Seconds 10 }
    }
    if (-not $connected) {
        throw "postprovision: could not connect to $($env:AZURE_SQL_SERVER_FQDN)/$($env:AZURE_SQL_DATABASE_NAME)."
    }

    # --- 2. Managed identity contained user (no Graph lookup) ----------------------------------------
    # `FROM EXTERNAL PROVIDER` would need the server identity to hold Directory Readers. Supplying the SID
    # (the client id as binary) lets the Entra admin create the user directly (spec 18.2).
    $miName = $env:AZURE_MANAGED_IDENTITY_NAME
    $clientId = $env:AZURE_MANAGED_IDENTITY_CLIENT_ID

    # Everything below is interpolated into T-SQL, so validate it first.
    if ($clientId -notmatch '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$') {
        throw 'postprovision: AZURE_MANAGED_IDENTITY_CLIENT_ID is not a GUID.'
    }
    $sidQuery = "SET NOCOUNT ON; SELECT CONVERT(varchar(34), CAST(CAST('$clientId' AS uniqueidentifier) AS varbinary(16)), 1)"
    $sid = ((sqlcmd @sqlcmdArgs -h -1 -W -Q $sidQuery) -join '').Trim()
    if ($sid -cnotmatch '^0x[0-9A-F]{32}$') {
        throw 'postprovision: unexpected SID format returned by SQL.'
    }
    if ($miName -notmatch '^[A-Za-z0-9_-]{1,128}$') {
        throw 'postprovision: AZURE_MANAGED_IDENTITY_NAME has characters that are not allowed.'
    }

    $sql = [System.Text.StringBuilder]::new()
    [void]$sql.AppendLine('SET NOCOUNT ON;')
    [void]$sql.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$miName')")
    [void]$sql.AppendLine("    CREATE USER [$miName] WITH SID = $sid, TYPE = E;")
    foreach ($role in 'db_datareader', 'db_datawriter', 'db_ddladmin') {
        [void]$sql.AppendLine('IF NOT EXISTS (SELECT 1 FROM sys.database_role_members rm')
        [void]$sql.AppendLine('    JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id')
        [void]$sql.AppendLine('    JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id')
        [void]$sql.AppendLine("    WHERE r.name = N'$role' AND m.name = N'$miName')")
        [void]$sql.AppendLine("    ALTER ROLE $role ADD MEMBER [$miName];")
    }
    $userScript = Join-Path $workDir 'create-user.sql'
    [System.IO.File]::WriteAllText($userScript, $sql.ToString())
    sqlcmd @sqlcmdArgs -i $userScript
    Write-Output "postprovision: managed identity user '$miName' is in place."

    # --- 3. Schema -----------------------------------------------------------------------------------
    $connection = "Server=tcp:$($env:AZURE_SQL_SERVER_FQDN),1433;Database=$($env:AZURE_SQL_DATABASE_NAME);Authentication=Active Directory Default;Encrypt=True"
    # Build once, quietly, so later `dotnet run` output can be captured without build chatter.
    dotnet build src/Nachos.Cli --nologo --verbosity quiet
    dotnet run --no-build --project src/Nachos.Cli -- schema upgrade --connection $connection

    # --- 4. Bootstrap signing secret + admin key -----------------------------------------------------
    $vault = $env:AZURE_KEY_VAULT_NAME
    # Listing (rather than `show`) fails loudly on auth errors, so an outage can never look like "absent"
    # and trigger an overwrite of an existing signing key.
    $existingNames = @(az keyvault secret list --vault-name $vault --query '[].name' --output tsv)

    if ($existingNames -contains 'nachos-bootstrap-admin-key') {
        Write-Output "postprovision: secret 'nachos-bootstrap-admin-key' already exists; nothing to bootstrap."
        return
    }

    $signingFile = Join-Path $workDir 'signing-key'
    if ($existingNames -contains 'nachos-signing-key-0') {
        az keyvault secret download --vault-name $vault --name nachos-signing-key-0 --file $signingFile --output none
        Write-Output "postprovision: reusing secret 'nachos-signing-key-0'."
    }
    else {
        $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
        $encoded = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        [System.IO.File]::WriteAllText($signingFile, $encoded)
        az keyvault secret set --vault-name $vault --name nachos-signing-key-0 --file $signingFile --output none
        Write-Output "postprovision: stored secret 'nachos-signing-key-0'."
    }

    $adminFile = Join-Path $workDir 'admin-key'
    $env:NACHOS_SIGNING_SECRET = Read-FileText $signingFile
    try {
        $minted = dotnet run --no-build --project src/Nachos.Cli -- keys create --admin --signing-secret-env NACHOS_SIGNING_SECRET
    }
    finally {
        Remove-Item Env:NACHOS_SIGNING_SECRET -ErrorAction SilentlyContinue
    }
    [System.IO.File]::WriteAllText($adminFile, (($minted -join '') -replace '[\r\n]', ''))
    az keyvault secret set --vault-name $vault --name nachos-bootstrap-admin-key --file $adminFile --output none
    Write-Output "postprovision: stored secret 'nachos-bootstrap-admin-key'."
}
finally {
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
    # Always remove the temporary firewall rule, even when an earlier step failed.
    if ($firewallRule) {
        try {
            az sql server firewall-rule delete `
                --resource-group $env:AZURE_RESOURCE_GROUP --server $env:AZURE_SQL_SERVER_NAME `
                --name $firewallRule --output none
        }
        catch {
            Write-Warning "postprovision: could not remove firewall rule '$firewallRule'; delete it manually."
        }
    }
}
