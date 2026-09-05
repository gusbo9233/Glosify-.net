#!/usr/bin/env bash
set -euo pipefail
vault_home="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
vault_repo="${1:-$(git rev-parse --show-toplevel)}"
exec dotnet "$vault_home/server/bin/Debug/net10.0/ProjectVault.dll" serve --repo "$vault_repo" --tool-root "$vault_home"
