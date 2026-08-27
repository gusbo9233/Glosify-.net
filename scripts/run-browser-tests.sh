#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <published-app-directory>" >&2
  exit 2
fi

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_directory/.." && pwd)"
publish_directory="$(cd "$1" && pwd)"
application_dll="$publish_directory/Glosify.dll"

if [[ ! -f "$application_dll" ]]; then
  echo "Published application not found: $application_dll" >&2
  exit 2
fi

require_environment_variable() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "$name must be set." >&2
    exit 2
  fi
}

require_environment_variable GLOSIFY_BROWSER_BASE_URL
require_environment_variable GLOSIFY_BROWSER_RUN_TOKEN

shopt -s nocasematch
if [[ ! "$GLOSIFY_BROWSER_BASE_URL" =~ ^https?://(localhost|127\.0\.0\.1|\[::1\])(:[0-9]+)?/?$ ]]; then
  echo "GLOSIFY_BROWSER_BASE_URL must be an origin-only absolute HTTP(S) loopback URL." >&2
  exit 2
fi
shopt -u nocasematch

if [[ "${REQUIRE_BROWSER_TESTS:-}" != "true" ]]; then
  echo "REQUIRE_BROWSER_TESTS must be set to true when using this launcher." >&2
  exit 2
fi

configuration="${GLOSIFY_BROWSER_CONFIGURATION:-Release}"
startup_attempts="${GLOSIFY_BROWSER_STARTUP_ATTEMPTS:-30}"
temporary_root="${TMPDIR:-/tmp}"
app_log="${GLOSIFY_BROWSER_APP_LOG:-$temporary_root/glosify-browser-$$.log}"
mkdir -p "$(dirname "$app_log")"

export ASPNETCORE_ENVIRONMENT=BrowserTesting
export ASPNETCORE_URLS="$GLOSIFY_BROWSER_BASE_URL"
export BrowserTests__RunToken="$GLOSIFY_BROWSER_RUN_TOKEN"

app_pid=""
cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
  exit "$status"
}
trap cleanup EXIT INT TERM

(
  cd "$publish_directory"
  exec dotnet ./Glosify.dll
) > "$app_log" 2>&1 &
app_pid=$!

handshake_url="${GLOSIFY_BROWSER_BASE_URL%/}/_test/browser-handshake"
ready=false
for ((attempt = 1; attempt <= startup_attempts; attempt++)); do
  if curl --fail --silent --show-error \
      --output /dev/null \
      --header "X-Glosify-Browser-Test-Token: $GLOSIFY_BROWSER_RUN_TOKEN" \
      "$handshake_url" 2>/dev/null; then
    ready=true
    break
  fi

  if ! kill -0 "$app_pid" 2>/dev/null; then
    echo "Published application exited before the browser-test handshake succeeded." >&2
    cat "$app_log" >&2
    exit 1
  fi

  sleep 2
done

if [[ "$ready" != "true" ]]; then
  echo "Published application did not become ready at $handshake_url." >&2
  cat "$app_log" >&2
  exit 1
fi

test_arguments=(
  "$repository_root/Glosify.BrowserTests/Glosify.BrowserTests.csproj"
  --configuration "$configuration"
  --no-build
  --no-restore
)
if [[ -n "${GLOSIFY_BROWSER_RESULTS_DIRECTORY:-}" ]]; then
  mkdir -p "$GLOSIFY_BROWSER_RESULTS_DIRECTORY"
  test_arguments+=(
    --logger "trx;LogFileName=glosify-browser.trx"
    --results-directory "$GLOSIFY_BROWSER_RESULTS_DIRECTORY"
  )
fi
if [[ -n "${GLOSIFY_BROWSER_TEST_FILTER:-}" ]]; then
  test_arguments+=(--filter "$GLOSIFY_BROWSER_TEST_FILTER")
fi

if dotnet test "${test_arguments[@]}"; then
  :
else
  test_status=$?
  echo "Browser journeys failed. Application log:" >&2
  cat "$app_log" >&2
  exit "$test_status"
fi
