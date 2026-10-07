#!/usr/bin/env bash
# azd postprovision hook (posix). Never run by agents or CI: it needs an owner-approved `azd provision`
# (spec 18.3) and an `az login` as the principal that is the SQL Entra admin set by Bicep.
#
#  1. Open the SQL firewall to this machine for the duration of the run.
#  2. Create the managed-identity contained user WITHOUT a Microsoft Graph lookup.
#  3. Apply the schema (`nachos schema upgrade`).
#  4. Bootstrap the signing secret and the first admin key in Key Vault.
#
# Secrets never appear in output or on a command line: values live in a 0700 temp directory, are
# handed to the CLIs by file or environment variable, and the directory is removed on exit.
set -euo pipefail
umask 077

required=(
  AZURE_SUBSCRIPTION_ID
  AZURE_RESOURCE_GROUP
  AZURE_KEY_VAULT_NAME
  AZURE_SQL_SERVER_NAME
  AZURE_SQL_SERVER_FQDN
  AZURE_SQL_DATABASE_NAME
  AZURE_MANAGED_IDENTITY_NAME
  AZURE_MANAGED_IDENTITY_CLIENT_ID
)
missing=()
for name in "${required[@]}"; do
  [[ -n "${!name:-}" ]] || missing+=("$name")
done
if ((${#missing[@]} > 0)); then
  echo "postprovision: missing azd environment values: ${missing[*]}" >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

work_dir="$(mktemp -d)"
firewall_rule=""

cleanup() {
  local status=$?
  rm -rf "$work_dir"
  # Always remove the temporary firewall rule, even when an earlier step failed.
  if [[ -n "$firewall_rule" ]]; then
    az sql server firewall-rule delete \
      --subscription "$AZURE_SUBSCRIPTION_ID" \
      --resource-group "$AZURE_RESOURCE_GROUP" --server "$AZURE_SQL_SERVER_NAME" \
      --name "$firewall_rule" --output none ||
      echo "postprovision: WARNING could not remove firewall rule '$firewall_rule'; delete it manually." >&2
  fi
  exit "$status"
}
trap cleanup EXIT

# --- 1. Temporary SQL firewall rule for this machine -------------------------------------------------
# The server only allows Azure-internal traffic, but this hook runs on a developer machine. That is also
# why running it is owner-approved: it briefly exposes the SQL endpoint to the caller's public IP.
public_ip="$(curl -fsS --max-time 15 https://api.ipify.org)"
if [[ ! "$public_ip" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then
  echo "postprovision: could not determine this machine's public IPv4 address." >&2
  exit 1
fi
firewall_rule="nachos-hook-$(date +%s)"
az sql server firewall-rule create \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$AZURE_RESOURCE_GROUP" --server "$AZURE_SQL_SERVER_NAME" \
  --name "$firewall_rule" --start-ip-address "$public_ip" --end-ip-address "$public_ip" --output none
echo "postprovision: added temporary firewall rule '$firewall_rule'."

sqlcmd_args=(-S "$AZURE_SQL_SERVER_FQDN" -d "$AZURE_SQL_DATABASE_NAME" --authentication-method ActiveDirectoryDefault -b)

# New firewall rules can take a while to take effect; probe until the database answers.
connected=false
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if sqlcmd "${sqlcmd_args[@]}" -Q "SELECT 1" >/dev/null 2>&1; then
    connected=true
    break
  fi
  sleep 10
done
if [[ "$connected" != true ]]; then
  echo "postprovision: could not connect to $AZURE_SQL_SERVER_FQDN/$AZURE_SQL_DATABASE_NAME." >&2
  exit 1
fi

# --- 2. Managed identity contained user (no Graph lookup) --------------------------------------------
# `FROM EXTERNAL PROVIDER` would need the server identity to hold Directory Readers. Supplying the SID
# (the client id as binary) lets the Entra admin create the user directly (spec 18.2).
mi_name="$AZURE_MANAGED_IDENTITY_NAME"
client_id="$AZURE_MANAGED_IDENTITY_CLIENT_ID"

# Everything below is interpolated into T-SQL, so validate it first.
guid_pattern='^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$'
if [[ ! "$client_id" =~ $guid_pattern ]]; then
  echo "postprovision: AZURE_MANAGED_IDENTITY_CLIENT_ID is not a GUID." >&2
  exit 1
fi
sid="$(sqlcmd "${sqlcmd_args[@]}" -h -1 -W \
  -Q "SET NOCOUNT ON; SELECT CONVERT(varchar(34), CAST(CAST('$client_id' AS uniqueidentifier) AS varbinary(16)), 1)" |
  tr -d '[:space:]')"
if [[ ! "$sid" =~ ^0x[0-9A-F]{32}$ ]]; then
  echo "postprovision: unexpected SID format returned by SQL." >&2
  exit 1
fi
if [[ ! "$mi_name" =~ ^[A-Za-z0-9_-]{1,128}$ ]]; then
  echo "postprovision: AZURE_MANAGED_IDENTITY_NAME has characters that are not allowed." >&2
  exit 1
fi

# A user left over from an earlier identity with the same name keeps its old SID, so the new identity could not
# sign in while the script below would skip it as "already there". Fail loudly instead of continuing.
existing_sid="$(sqlcmd "${sqlcmd_args[@]}" -h -1 -W \
  -Q "SET NOCOUNT ON; SELECT CONVERT(varchar(34), sid, 1) FROM sys.database_principals WHERE name = N'$mi_name'" |
  tr -d '[:space:]')"
if [[ -n "$existing_sid" && "$existing_sid" != "$sid" ]]; then
  echo "postprovision: database user '$mi_name' exists with SID $existing_sid, not the expected $sid: the managed identity was recreated. An admin must drop the stale user ([$mi_name]) before re-running." >&2
  exit 1
fi

user_script="$work_dir/create-user.sql"
{
  echo "SET NOCOUNT ON;"
  echo "IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$mi_name')"
  echo "    CREATE USER [$mi_name] WITH SID = $sid, TYPE = E;"
  for role in db_datareader db_datawriter db_ddladmin; do
    echo "IF NOT EXISTS (SELECT 1 FROM sys.database_role_members rm"
    echo "    JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id"
    echo "    JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id"
    echo "    WHERE r.name = N'$role' AND m.name = N'$mi_name')"
    echo "    ALTER ROLE $role ADD MEMBER [$mi_name];"
  done
} >"$user_script"
sqlcmd "${sqlcmd_args[@]}" -i "$user_script"
echo "postprovision: managed identity user '$mi_name' is in place."

# --- 3. Schema ---------------------------------------------------------------------------------------
connection="Server=tcp:$AZURE_SQL_SERVER_FQDN,1433;Database=$AZURE_SQL_DATABASE_NAME;Authentication=Active Directory Default;Encrypt=True"
# Build once, quietly, so the later runs (--no-build) print nothing but the CLI output.
dotnet build src/Nachos.Cli --nologo --verbosity quiet
dotnet run --no-build --no-launch-profile --project src/Nachos.Cli -- schema upgrade --connection "$connection"

# --- 4. Bootstrap signing secret + admin key ---------------------------------------------------------
vault="$AZURE_KEY_VAULT_NAME"
# Listing (rather than `show`) fails loudly on auth errors, so an outage can never look like "absent"
# and trigger an overwrite of an existing signing key.
# The Secrets Officer role is assigned in this same provision and can take minutes to propagate, so retry
# (up to 30 x 10 s) ONLY on authorization failures; any other failure aborts immediately.
list_error="$work_dir/keyvault-list.err"
existing_names=""
listed=false
for _ in $(seq 1 30); do
  if existing_names="$(az keyvault secret list --subscription "$AZURE_SUBSCRIPTION_ID" --vault-name "$vault" --query "[].name" --output tsv 2>"$list_error")"; then
    listed=true
    break
  fi
  if grep -qiE 'Forbidden|AuthorizationFailed|AuthorizationPermissionMismatch' "$list_error"; then
    sleep 10
    continue
  fi
  cat "$list_error" >&2
  echo "postprovision: listing Key Vault secrets failed." >&2
  exit 1
done
if [[ "$listed" != true ]]; then
  cat "$list_error" >&2
  echo "postprovision: still not authorized to list Key Vault secrets after 5 minutes; role assignment may not have propagated." >&2
  exit 1
fi

if grep -qx 'nachos-bootstrap-admin-key' <<<"$existing_names"; then
  echo "postprovision: secret 'nachos-bootstrap-admin-key' already exists; nothing to bootstrap."
  exit 0
fi

signing_file="$work_dir/signing-key"
if grep -qx 'nachos-signing-key-0' <<<"$existing_names"; then
  az keyvault secret download --subscription "$AZURE_SUBSCRIPTION_ID" --vault-name "$vault" --name nachos-signing-key-0 --file "$signing_file" --output none
  echo "postprovision: reusing secret 'nachos-signing-key-0'."
else
  head -c 48 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n' >"$signing_file"
  az keyvault secret set --subscription "$AZURE_SUBSCRIPTION_ID" --vault-name "$vault" --name nachos-signing-key-0 --file "$signing_file" --output none
  echo "postprovision: stored secret 'nachos-signing-key-0'."
fi

raw_admin_file="$work_dir/admin-key.raw"
admin_file="$work_dir/admin-key"
# --no-launch-profile: a launch profile makes the CLI host print a banner on stdout, which would be captured
# along with the key. --kid 0: the key is signed with ring entry 0, i.e. nachos-signing-key-0.
NACHOS_SIGNING_SECRET="$(<"$signing_file")" \
  dotnet run --no-build --no-launch-profile --project src/Nachos.Cli -- keys create --admin --kid 0 --signing-secret-env NACHOS_SIGNING_SECRET >"$raw_admin_file"
minted="$(<"$raw_admin_file")"
minted="${minted#"${minted%%[![:space:]]*}"}"
minted="${minted%"${minted##*[![:space:]]}"}"
# Store only something shaped like a JWT (three base64url segments); never echo what was returned.
jwt_shape='^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$'
if [[ ! "$minted" =~ $jwt_shape ]]; then
  echo "postprovision: the CLI did not return a well-formed admin key; nothing was stored." >&2
  exit 1
fi
printf '%s' "$minted" >"$admin_file"
az keyvault secret set --subscription "$AZURE_SUBSCRIPTION_ID" --vault-name "$vault" --name nachos-bootstrap-admin-key --file "$admin_file" --output none
echo "postprovision: stored secret 'nachos-bootstrap-admin-key'."
