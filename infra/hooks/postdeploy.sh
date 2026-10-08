#!/usr/bin/env bash
# azd postdeploy hook (posix): smoke-test the freshly deployed API. Only a GET; touches no secrets.
# The real API's /health/ready answers with the default ASP.NET Core writer's plain-text "Healthy"; the
# placeholder echo server answers 200 with a JSON dump of the request, so only the exact body proves that the
# deployed image (not the placeholder) is serving.
# If postprovision succeeded but `azd deploy` failed or was skipped, the public placeholder stays live
# (it echoes request headers); redeploy with `azd deploy`, or `azd down` if that is not possible.
set -euo pipefail

: "${NACHOS_API_URI:?postdeploy: NACHOS_API_URI is not set}"

# Poll until the status is 200 and the body is exactly "Healthy". azd waits for the ARM operation, not for the
# traffic switch, so the first answers can still come from the placeholder revision (or a warming API: Degraded, or
# 503 Unhealthy) and are retried. The body of a non-2xx answer is kept too (no -f; the status comes from -w), so the
# failure report shows what the API last said. --max-time bounds each attempt; the budget is attempts x delay.
readonly attempts=10
readonly delay_seconds=6
body=""
status=""
for ((attempt = 1; attempt <= attempts; attempt++)); do
  if response="$(curl -sS --max-time 30 -w '\n%{http_code}' "${NACHOS_API_URI%/}/health/ready")"; then
    status="${response##*$'\n'}"
    body="${response%$'\n'*}"
  else
    status=""
    body=""
  fi
  trimmed="${body#"${body%%[![:space:]]*}"}"
  trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
  if [[ "$status" == 200 && "$trimmed" == "Healthy" ]]; then
    echo "postdeploy: $NACHOS_API_URI is ready (the deployed API reports Healthy)."
    exit 0
  fi
  if ((attempt < attempts)); then
    sleep "$delay_seconds"
  fi
done

# Show at most 200 printable characters of the last answer; never the raw body.
shown="$(printf '%s' "$body" | head -c 200 | tr -cd '[:print:]')"
echo "postdeploy: after $attempts attempts /health/ready last answered '$shown'." >&2
echo 'postdeploy: The placeholder image is still serving (or the API is not healthy). Re-run `azd deploy`; if that cannot succeed, run `azd down`.' >&2
exit 1
