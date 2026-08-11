#!/usr/bin/env bash
set -euo pipefail

resource_group="${GLOSIFY_RESOURCE_GROUP:-glosify}"
app_name="${GLOSIFY_APP_NAME:-glosify-app}"
workspace_name="${GLOSIFY_LOG_WORKSPACE:-ws-b08575d2-swedencent}"
setting_name="${GLOSIFY_DIAGNOSTIC_SETTING:-glosify-app-operational}"

app_id="$(az webapp show \
  --resource-group "$resource_group" \
  --name "$app_name" \
  --query id \
  --output tsv)"
workspace_id="$(az monitor log-analytics workspace list \
  --query "[?name=='$workspace_name'].id | [0]" \
  --output tsv)"

if [[ -z "$app_id" ]]; then
  echo "App Service '$app_name' was not found in resource group '$resource_group'." >&2
  exit 1
fi
if [[ -z "$workspace_id" ]]; then
  echo "Log Analytics workspace '$workspace_name' was not found in the active subscription." >&2
  exit 1
fi

az webapp config set \
  --resource-group "$resource_group" \
  --name "$app_name" \
  --generic-configurations '{"healthCheckPath":"/healthz"}' \
  --output none

az monitor diagnostic-settings create \
  --name "$setting_name" \
  --resource "$app_id" \
  --workspace "$workspace_id" \
  --export-to-resource-specific true \
  --logs '[{"category":"AppServiceHTTPLogs","enabled":true},{"category":"AppServiceConsoleLogs","enabled":true},{"category":"AppServiceAuditLogs","enabled":true},{"category":"AppServiceIPSecAuditLogs","enabled":true},{"category":"AppServicePlatformLogs","enabled":true},{"category":"AppServiceAuthenticationLogs","enabled":true}]' \
  --metrics '[{"category":"AllMetrics","enabled":true}]' \
  --output none

health_path="$(az webapp config show \
  --resource-group "$resource_group" \
  --name "$app_name" \
  --query healthCheckPath \
  --output tsv)"
if [[ "$health_path" != "/healthz" ]]; then
  echo "Health Check verification failed: expected /healthz, got '$health_path'." >&2
  exit 1
fi

az monitor diagnostic-settings show \
  --name "$setting_name" \
  --resource "$app_id" \
  --output table

echo "Configured /healthz and diagnostic setting '$setting_name' for '$app_name'."
