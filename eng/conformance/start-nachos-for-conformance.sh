#!/usr/bin/env bash
# Starts the Nachos API (in-memory provider) for the upstream-SDK conformance suites and writes the suites' environment
# file. With --run it also runs both suites and always tears the API down; without it, the API stays up until this
# script is interrupted. Twin: Start-NachosForConformance.ps1.
#
# Auth is strict by default: the script starts the API with authentication enforced and probes it with an admin key.
# If the probe is refused it prints an error and exits non-zero before any suite runs, so a CI gate cannot go green
# on an authentication regression. --require-auth selects that default explicitly, so a CI line documents itself
# (`--run --require-auth`); it cannot be combined with --allow-auth-disabled (usage error, exit 2). --allow-auth-disabled is the development-only escape hatch for a branch where auth
# is not published yet (every /v3 route still fails closed): the script then restarts the API with authentication
# disabled (allowed in Development only) and records NACHOS_AUTH_MODE=disabled, so the scoped-key scenario reports
# "not executed" rather than passing against an open server.
#
# Hermetic: the API and the CLI key-minting commands run with an allow-listed environment (PATH, HOME, DOTNET_ROOT,
# LANG, LC_ALL, TMPDIR, the two DOTNET_* opt-outs and the synthetic settings this script sets itself). Everything else
# the caller exported is dropped, so an ambient SQL, Key Vault, Entra, Azure or telemetry setting cannot redirect
# the in-memory, offline run. (The build steps and the suites keep the caller's environment: they need its package
# feeds and proxies.)
#
# Usage: start-nachos-for-conformance.sh [--run] [--require-auth | --allow-auth-disabled] [--env-file PATH] [--timeout SECONDS]
set -euo pipefail

usage() { sed -n '/^# Usage:/s/^# //p' "${BASH_SOURCE[0]}" >&2; }

run=0
require_auth=0
allow_auth_disabled=0
env_file=""
timeout_seconds=90
while [[ $# -gt 0 ]]; do
  case "$1" in
    --run) run=1; shift ;;
    --require-auth) require_auth=1; shift ;;
    --allow-auth-disabled) allow_auth_disabled=1; shift ;;
    --env-file) env_file="${2:?--env-file needs a path}"; shift 2 ;;
    --timeout) timeout_seconds="${2:?--timeout needs a number of seconds}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "error: unknown argument '$1'" >&2; usage; exit 2 ;;
  esac
done
if ((require_auth && allow_auth_disabled)); then
  echo "error: --require-auth and --allow-auth-disabled contradict each other; pass at most one" >&2
  usage
  exit 2
fi
[[ "$timeout_seconds" =~ ^[0-9]+$ ]] || { echo "error: --timeout must be a whole number of seconds" >&2; exit 2; }

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
umask 077
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/nachos-conformance.XXXXXX")"
env_file="${env_file:-$work_dir/conformance.env}"
api_log="$work_dir/api.log"
api_pid=""

stop_api() {
  [[ -n "$api_pid" ]] || return 0
  if kill -0 "$api_pid" 2>/dev/null; then
    kill "$api_pid" 2>/dev/null || true
    for _ in $(seq 1 100); do
      kill -0 "$api_pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -9 "$api_pid" 2>/dev/null || true
  fi
  wait "$api_pid" 2>/dev/null || true
  api_pid=""
}

cleanup() {
  stop_api
  rm -rf "$work_dir"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

fail() { echo "error: $*" >&2; exit 1; }

# Build once; the output paths are asked of MSBuild rather than guessed.
build_output() {
  dotnet msbuild "$repo_root/src/$1/$1.csproj" -p:Configuration=Release -getProperty:TargetPath | tr -d '\r'
}
dotnet build "$repo_root/src/Nachos.Api/Nachos.Api.csproj" -c Release -v q --nologo >&2 || fail "building Nachos.Api failed"
dotnet build "$repo_root/src/Nachos.Cli/Nachos.Cli.csproj" -c Release -v q --nologo >&2 || fail "building Nachos.Cli failed"
api_dll="$(build_output Nachos.Api)"
cli_dll="$(build_output Nachos.Cli)"

# The signing secret is exported only into the API and CLI processes below; it is never printed or written.
conformance_secret="$(openssl rand -base64 32)"

# Replaces the calling process (call it inside a subshell) with "$2…" running under the allow-listed environment.
# $1 names the extra variables to keep, space separated. Variables are unset rather than passed to `env -i` so that
# the signing secret never appears on a command line, and exported shell functions are dropped too.
hermetic_exec() {
  local keep=" PATH HOME DOTNET_ROOT DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_NOLOGO LANG LC_ALL TMPDIR $1 "
  shift
  local name
  for name in $(compgen -e); do
    [[ "$keep" == *" $name "* ]] || unset "$name" 2>/dev/null || true
  done
  for name in $(compgen -A function); do
    unset -f "$name"
  done
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  exec "$@"
}

mint_key() (
  export NACHOS_CONFORMANCE_SECRET="$conformance_secret"
  hermetic_exec "NACHOS_CONFORMANCE_SECRET" \
    dotnet "$cli_dll" keys create --signing-secret-env NACHOS_CONFORMANCE_SECRET --kid dev "$@"
)

# Starts the API on a kernel-chosen port (no race for a free one) and waits for /health/ready.
# Sets api_pid and base_url.
start_api() {
  local auth_enabled="$1"
  : > "$api_log"
  (
    cd "$(dirname "$api_dll")"
    export ASPNETCORE_ENVIRONMENT=Development
    export ASPNETCORE_URLS="http://127.0.0.1:0"
    export Nachos__Auth__Enabled="$auth_enabled"
    export Nachos__Auth__NachosKey__Keys__0__Kid=dev
    export Nachos__Auth__NachosKey__Keys__0__Secret="$conformance_secret"
    hermetic_exec "ASPNETCORE_ENVIRONMENT ASPNETCORE_URLS Nachos__Auth__Enabled Nachos__Auth__NachosKey__Keys__0__Kid Nachos__Auth__NachosKey__Keys__0__Secret" \
      dotnet "$api_dll"
  ) >"$api_log" 2>&1 &
  api_pid=$!

  local deadline=$((SECONDS + timeout_seconds))
  base_url=""
  while ((SECONDS < deadline)); do
    kill -0 "$api_pid" 2>/dev/null || { tail -n 20 "$api_log" >&2; fail "the API exited during startup"; }
    if [[ -z "$base_url" ]]; then
      base_url="$(grep -o -m1 'http://127\.0\.0\.1:[0-9]*' "$api_log" || true)"
    fi
    if [[ -n "$base_url" ]] && curl -fsS -o /dev/null --max-time 2 "$base_url/health/ready" 2>/dev/null; then
      return 0
    fi
    sleep 0.2
  done
  tail -n 20 "$api_log" >&2
  fail "the API was not ready within ${timeout_seconds}s"
}

admin_key="$(mint_key --admin)"
peer_key="$(mint_key --workspace conformance-auth --peer member)"

# The key travels on curl's stdin (-K -), not its command line.
admin_status() {
  printf 'header = "Authorization: Bearer %s"\n' "$admin_key" |
    curl -sS -o /dev/null -w '%{http_code}' --max-time 5 -K - -X POST \
      -H 'Content-Type: application/json' -d '{}' "$base_url/v3/workspaces/list" || true
}

auth_mode=enforced
start_api true
probe_status="$(admin_status)"
if [[ "$probe_status" != 200 ]]; then
  if ((allow_auth_disabled == 0)); then
    echo "error: authentication does not work: the API answered an admin key with HTTP $probe_status instead of 200." >&2
    echo "       No suite was run. Pass --allow-auth-disabled only while auth is not published on this branch." >&2
    exit 1
  fi
  echo "note: the API answered an admin key with HTTP $probe_status; --allow-auth-disabled restarts it with authentication disabled (scoped-key scenario will not execute)" >&2
  stop_api
  auth_mode=disabled
  start_api false
fi

{
  printf 'NACHOS_BASE_URL=%s\n' "$base_url"
  printf 'NACHOS_AUTH_MODE=%s\n' "$auth_mode"
  printf 'NACHOS_ADMIN_KEY=%s\n' "$admin_key"
  printf 'NACHOS_PEER_KEY=%s\n' "$peer_key"
  printf 'NACHOS_AUTH_WORKSPACE=conformance-auth\n'
  printf 'NACHOS_AUTH_PEER=member\n'
} > "$env_file"
chmod 600 "$env_file"
echo "Nachos is ready at $base_url (auth: $auth_mode); environment file: $env_file" >&2

if ((run == 0)); then
  echo "Press Ctrl+C to stop the API." >&2
  wait "$api_pid" || true
  exit 0
fi

# Both suites always run, so one language's failure does not hide the other's.
status=0
python_dir="$repo_root/test/conformance/python"
typescript_dir="$repo_root/test/conformance/typescript"

echo "== Python (honcho-ai) ==" >&2
if python3 -m venv "$work_dir/venv" &&
   "$work_dir/venv/bin/python" -m pip install -q --disable-pip-version-check --require-hashes -r "$python_dir/requirements.lock"; then
  (cd "$python_dir" && set -a && . "$env_file" && set +a && "$work_dir/venv/bin/python" -m pytest -rs) || status=1
else
  echo "error: installing the Python suite failed" >&2; status=1
fi

echo "== TypeScript (@honcho-ai/sdk) ==" >&2
if (cd "$typescript_dir" && npm ci --ignore-scripts --no-audit --no-fund --silent); then
  (cd "$typescript_dir" && set -a && . "$env_file" && set +a && node --test --test-reporter=spec) || status=1
else
  echo "error: installing the TypeScript suite failed" >&2; status=1
fi

exit "$status"
