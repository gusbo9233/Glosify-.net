#!/usr/bin/env bash
set -euo pipefail

subscription_id="${GLOSIFY_SUBSCRIPTION_ID:?Set GLOSIFY_SUBSCRIPTION_ID to the approved production subscription ID.}"
resource_group="${GLOSIFY_RESOURCE_GROUP:-glosify}"
app_name="${GLOSIFY_APP_NAME:-glosify-app}"
workspace_name="${GLOSIFY_LOG_WORKSPACE:-ws-b08575d2-swedencent}"
setting_name="${GLOSIFY_DIAGNOSTIC_SETTING:-glosify-app-operational}"

if ! az account show --subscription "$subscription_id" --output none; then
  echo "Azure subscription '$subscription_id' is not accessible." >&2
  exit 1
fi

if ! app_id="$(az webapp show \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --name "$app_name" \
  --query id \
  --output tsv 2>/dev/null)" || [[ -z "$app_id" ]]; then
  echo "App Service '$app_name' was not found in resource group '$resource_group'." >&2
  exit 1
fi

if ! workspace_ids="$(az monitor log-analytics workspace list \
  --subscription "$subscription_id" \
  --query "[?name=='$workspace_name'].id" \
  --output tsv)"; then
  echo "Could not list Log Analytics workspaces in subscription '$subscription_id'." >&2
  exit 1
fi
workspace_count="$(printf '%s\n' "$workspace_ids" | awk 'NF { count++ } END { print count + 0 }')"
if [[ "$workspace_count" -ne 1 ]]; then
  echo "Expected exactly one Log Analytics workspace named '$workspace_name' in subscription '$subscription_id'; found $workspace_count." >&2
  exit 1
fi
workspace_id="$(printf '%s\n' "$workspace_ids" | awk 'NF { print; exit }')"

if ! application_log_path="$(az webapp config appsettings list \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --name "$app_name" \
  --query "[?name=='APPLICATIONINSIGHTS_CONNECTION_STRING' && value!=''].name | [0]" \
  --output tsv)" || [[ "$application_log_path" != "APPLICATIONINSIGHTS_CONNECTION_STRING" ]]; then
  echo "APPLICATIONINSIGHTS_CONNECTION_STRING must be configured before AppServiceAppLogs can be excluded." >&2
  exit 1
fi

az webapp config set \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --name "$app_name" \
  --generic-configurations '{"healthCheckPath":"/healthz"}' \
  --output none

az monitor diagnostic-settings create \
  --subscription "$subscription_id" \
  --name "$setting_name" \
  --resource "$app_id" \
  --workspace "$workspace_id" \
  --export-to-resource-specific true \
  --logs '[{"category":"AppServiceHTTPLogs","enabled":true},{"category":"AppServiceConsoleLogs","enabled":true},{"category":"AppServiceAuditLogs","enabled":true},{"category":"AppServiceIPSecAuditLogs","enabled":true},{"category":"AppServicePlatformLogs","enabled":true},{"category":"AppServiceAuthenticationLogs","enabled":true}]' \
  --metrics '[{"category":"AllMetrics","enabled":true}]' \
  --output none

health_path="$(az webapp config show \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --name "$app_name" \
  --query healthCheckPath \
  --output tsv)"
if [[ "$health_path" != "/healthz" ]]; then
  echo "Health Check verification failed: expected /healthz, got '$health_path'." >&2
  exit 1
fi

az monitor diagnostic-settings show \
  --subscription "$subscription_id" \
  --name "$setting_name" \
  --resource "$app_id" \
  --output table

echo "Configured /healthz and diagnostic setting '$setting_name' for '$app_name'."
