#!/usr/bin/env bash
#
# Export every Microsoft Foundry agent version the application is configured to use into
# .foundry/agents/, so the instructions and tool declarations the models actually run on
# have a diffable record in this repository.
#
# This is an export, not a publish. Editing the JSON here changes nothing: agents are
# authored in the Foundry portal, and configuration pins the version. Re-run after
# publishing a new version and bumping appsettings.json, then commit the result.
#
# Usage:  tools/foundry/export-agents.sh [output-dir]
# Needs:  az login (an account with at least Foundry User on both projects), python3
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SETTINGS="$REPO_ROOT/Glosify/appsettings.json"
OUT_DIR="${1:-$REPO_ROOT/.foundry/agents}"
API_VERSION="2025-11-15-preview"

[ -f "$SETTINGS" ] || { echo "Could not find $SETTINGS" >&2; exit 1; }

echo "Requesting a Foundry token..."
TOKEN="$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)"
[ -n "$TOKEN" ] || { echo "Could not acquire a token. Run 'az login' first." >&2; exit 1; }

mkdir -p "$OUT_DIR"

# appsettings.json carries // comments, which the ASP.NET Core JSON configuration provider
# allows but json.loads does not, so strip them before parsing.
AGENTS="$(python3 - "$SETTINGS" <<'PY'
import json, re, sys

raw = open(sys.argv[1], encoding="utf-8").read()
raw = re.sub(r'^\s*//.*$', '', raw, flags=re.MULTILINE)
config = json.loads(raw)

def endpoint_of(url):
    return (url or "").rstrip("/")

rows = []

foundry = config.get("GenerativeAi", {}).get("Foundry", {})
assistant_endpoint = endpoint_of(foundry.get("ProjectEndpoint"))
for agent in (foundry.get("Agents") or {}).values():
    name, version = (agent or {}).get("Name"), (agent or {}).get("Version")
    if name and version and assistant_endpoint:
        rows.append((assistant_endpoint, name, str(version)))

speaking = config.get("Speaking", {})
speaking_endpoint = endpoint_of(speaking.get("ProjectEndpoint"))
for agent in (speaking.get("Agents") or {}).values():
    name, version = (agent or {}).get("Name"), (agent or {}).get("Version")
    if name and version and speaking_endpoint:
        rows.append((speaking_endpoint, name, str(version)))

# One agent can back several avatars (the tutor does); export it once.
for row in dict.fromkeys(rows):
    print("\t".join(row))
PY
)"

[ -n "$AGENTS" ] || { echo "No agents configured in $SETTINGS" >&2; exit 1; }

exported=0
failed=0
while IFS=$'\t' read -r endpoint name version; do
  [ -n "$name" ] || continue
  url="$endpoint/agents/$name/versions/$version?api-version=$API_VERSION"
  target="$OUT_DIR/$name-v$version.json"

  if ! body="$(curl -sS -f -H "Authorization: Bearer $TOKEN" "$url" 2>/dev/null)"; then
    echo "  MISSING  $name v$version" >&2
    failed=$((failed + 1))
    continue
  fi

  # Sort keys and pin the indent so re-running produces a clean diff rather than noise.
  printf '%s' "$body" | python3 -c '
import json, sys
document = json.load(sys.stdin)
# Volatile server-side fields would churn the diff on every export.
for field in ("created_at", "updated_at", "object"):
    document.pop(field, None)
json.dump(document, sys.stdout, indent=2, sort_keys=True, ensure_ascii=False)
sys.stdout.write("\n")
' > "$target"

  echo "  exported $name v$version"
  exported=$((exported + 1))
done <<< "$AGENTS"

echo
echo "Exported $exported agent version(s) to $OUT_DIR"
if [ "$failed" -gt 0 ]; then
  echo "$failed configured agent version(s) could not be read — check the version is published." >&2
  exit 1
fi
