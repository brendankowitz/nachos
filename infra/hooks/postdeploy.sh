#!/usr/bin/env bash
# azd postdeploy hook (posix): smoke-test the freshly deployed API. Only a GET; touches no secrets.
set -euo pipefail

: "${NACHOS_API_URI:?postdeploy: NACHOS_API_URI is not set}"

# The new revision can take a moment to become ready, hence the retries.
curl -fsS --retry 10 --retry-delay 6 --retry-connrefused "${NACHOS_API_URI%/}/health/ready"
echo
echo "postdeploy: $NACHOS_API_URI is ready."
