#!/usr/bin/env bash
set -euo pipefail
vault_home="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$vault_home"
npm ci
npm run build
dotnet restore server/server.csproj --locked-mode
dotnet build server/server.csproj --no-restore
printf 'Project Vault built. Start with: bash %s/scripts/start.sh /path/to/repository\n' "$vault_home"
