#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."
image="nachos-ci-fts:local"
container=""
cleanup() {
  status=$?
  trap - EXIT
  if [[ -n "$container" ]]; then
    if ! docker rm -f "$container" >/dev/null; then status=1; fi
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# This is the existing pinned two-stage recipe, not a replacement stock image.
docker build --tag "$image" eng/docker/mssql-fts
MSSQL_SA_PASSWORD="Aa1!$(openssl rand -hex 24)"
export MSSQL_SA_PASSWORD
export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
printf '::add-mask::%s\n' "$MSSQL_SA_PASSWORD"
container="$(docker run --detach --rm -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD "$image")"
ready=0
for _ in $(seq 1 90); do
  if docker exec -e SQLCMDPASSWORD "$container" /opt/mssql-tools18/bin/sqlcmd \
      -S localhost -U sa -C -b -l 2 -Q 'SELECT 1' >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 2
done
if ((ready != 1)); then echo 'FTS SQL container did not become ready.' >&2; exit 1; fi
docker exec -e SQLCMDPASSWORD "$container" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -C -b -l 5 -Q "
IF CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) <> 17
  THROW 51000, 'Expected SQL Server 2025', 1;
IF COALESCE(CONVERT(int, FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')), 0) <> 1
  THROW 51001, 'Full-Text Search is not installed', 1;
SELECT SERVERPROPERTY('ProductVersion') AS ProductVersion,
       FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS FullTextInstalled;"
