#!/usr/bin/env bash
# azd postdeploy hook (posix): smoke-test the freshly deployed API. Only a GET; touches no secrets.
# The real API's /health/ready answers with the default ASP.NET Core writer's plain-text "Healthy"; the
# placeholder echo server answers 200 with a JSON dump of the request, so only the exact body proves that the
# deployed image (not the placeholder) is serving.
# If postprovision succeeded but `azd deploy` failed or was skipped, the public placeholder stays live
# (it echoes request headers); redeploy with `azd deploy`, or `azd down` if that is not possible.
set -euo pipefail

: "${NACHOS_API_URI:?postdeploy: NACHOS_API_URI is not set}"

# The new revision can take a moment to become ready, hence the retries; --max-time bounds each attempt.
body="$(curl -fsS --max-time 30 --retry 10 --retry-delay 6 --retry-connrefused "${NACHOS_API_URI%/}/health/ready")" || body=""
trimmed="${body#"${body%%[![:space:]]*}"}"
trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
if [[ "$trimmed" != Healthy ]]; then
  # Show at most 200 printable characters of what answered; never the raw body.
  shown="$(printf '%s' "$body" | head -c 200 | tr -cd '[:print:]')"
  echo "postdeploy: /health/ready answered '$shown'." >&2
  echo 'postdeploy: The placeholder image is still serving (or the API is not healthy). Re-run `azd deploy`; if that cannot succeed, run `azd down`.' >&2
  exit 1
fi
echo "postdeploy: $NACHOS_API_URI is ready (the deployed API reports Healthy)."
