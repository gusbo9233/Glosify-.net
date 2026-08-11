# Production deployment runbook

This runbook covers the existing Glosify production deployment. It is an
operator checklist, not an infrastructure-provisioning guide.

Glosify does not use Azure Developer CLI (`azd`) or an
`.azure/deployment-plan.md`. The source of truth for delivery is
[`master_glosify.yml`](../.github/workflows/master_glosify.yml). For design
context, see the [architecture document](ARCHITECTURE.md) and
[ADR 0001](adr/0001-single-instance-state.md).

## Production targets

| Item | Production value |
|---|---|
| Public site and client base URL | `https://glosify.se` |
| Azure Web App | `glosify-app` |
| Azure resource group | `glosify` |
| App Service slot | `Production` |
| Deployment branch | `master` |
| GitHub environment | `production` |
| Budget period | Europe/Stockholm calendar month |
| Application AI ceiling | 300 SEK |

Use `https://glosify.se` for user-facing links and OAuth flows. The
`azurewebsites.net` hostname is useful for direct health checks, but Google
OAuth is registered for the custom domain.

## How deployment works

The workflow has two jobs:

1. `build` runs for pull requests, pushes to `master`, and manual workflow
   dispatches. It restores and audits packages, builds the solution, applies
   all EF Core migrations to an empty SQL Server, checks for model drift, runs
   backend, JavaScript, and Playwright tests, publishes the web app, and builds
   a Linux EF migration bundle.
2. `deploy` runs only for a push to `master`. It signs in to Azure through
   GitHub OIDC, applies the reviewed migration bundle to production, and then
   deploys the web artifact to `glosify-app`.

The ordering is deliberate:

```text
PR checks -> merge to master -> build -> production migration -> web deployment
```

If the build or production migration fails, the new web package is not
deployed. A manual `workflow_dispatch` validates and creates artifacts but does
not deploy production.

## Required GitHub configuration

The production job must be able to resolve these GitHub secret names. Prefer
the `production` environment for production-only values. Store values only in
GitHub or Azure, never in this repository:

- `AZUREAPPSERVICE_CLIENTID_059CC241545944E3BB4D754796025F63`
- `AZUREAPPSERVICE_TENANTID_210765179BDB40158ADE7F23514669FF`
- `AZUREAPPSERVICE_SUBSCRIPTIONID_EE07FD7E4DAF4E32BF67F3AFE307E71F`
- `PRODUCTION_SQL_CONNECTION_STRING`

The Azure OIDC identity `identity-glosify` owns schema changes and holds the
required database DDL permission. The runtime `glosify-app` managed identity is
separate and should retain only application runtime permissions, including
`db_datareader` and `db_datawriter` for Azure SQL.

## Safety-critical production settings

Before a release that changes authentication, legal disclosures, providers, or
cost controls, verify the following Azure App Service settings:

- `ASPNETCORE_ENVIRONMENT=Production`
- `Legal__ControllerName` contains the public controller name.
- `Legal__ContactEmail` contains the public support/privacy address.
- `ExtensionAuth__AllowedRedirectUris__0` exactly matches the pinned extension
  callback: `https://akepdpjieiokffdapibipomhbplikock.chromiumapp.org/glosify`.
- `OPENAI_API_KEY` is absent.
- `AZURE_CLIENT_ID` is absent when the system-assigned managed identity is
  intended. Production code then uses `ManagedIdentityCredential` for Foundry.
- The `glosify-app` system-assigned identity is enabled.

Economical subtitles remain disabled until all of these are configured together:

- `RealtimeTranslation__EconomicalEnabled=true`
- `RealtimeTranslation__SpeechEndpoint` is the Azure Speech custom-domain root.
- `RealtimeTranslation__TranslatorEndpoint` is either the exact global
  Translator endpoint or an Azure AI custom-domain root.
- With the global endpoint, `RealtimeTranslation__TranslatorResourceId` is the
  full `/subscriptions/.../providers/Microsoft.CognitiveServices/accounts/...`
  resource ID. Set `RealtimeTranslation__TranslatorRegion` as well when the
  selected Azure resource type requires the regional routing header.
- The `glosify-app` managed identity has the least-privilege Cognitive Services
  data-plane role required on both resources.

Do not lower the economical `AudioSekPerMinute` safety price below `0.35`
without comparing metered Speech audio time and Translator characters against
an actual Azure invoice. Auto detection intentionally contains only Estonian,
German, Polish, and Ukrainian so it stays within Azure Speech's four-language
at-start limit.

The checked-in [`appsettings.json`](../Glosify/appsettings.json) is the default
for the 300 SEK monthly application ledger. If
`AiUsage__MonthlyBudget__LimitSek` is present as an App Service override, it
must also be `300` unless a separately reviewed change intentionally alters the
ceiling. The application fails startup when an enabled provider cannot be
priced by the budget configuration.

The Gemini provider is only a rollback seam. Do not set
`GenerativeAi__Provider=Gemini` unless all Gemini models have configured budget
prices, `gemini` is included in the budgeted providers, and the Gemini
credential is configured. Startup is intentionally fail-closed otherwise.

Useful read-only checks:

```bash
az webapp identity show \
  --resource-group glosify \
  --name glosify-app \
  --query '{type:type,principalId:principalId}'

az webapp config appsettings list \
  --resource-group glosify \
  --name glosify-app \
  --query "[?name=='ASPNETCORE_ENVIRONMENT' || name=='Legal__ControllerName' || name=='Legal__ContactEmail' || name=='ExtensionAuth__AllowedRedirectUris__0' || name=='AiUsage__MonthlyBudget__LimitSek'].{name:name,value:value}"

az webapp config appsettings list \
  --resource-group glosify \
  --name glosify-app \
  --query "[?name=='OPENAI_API_KEY'].name"
```

The last command should return an empty array. Avoid listing all App Service
setting values in terminals, tickets, or CI logs because unrelated settings may
contain credentials.

## Pre-deployment checklist

1. Confirm the change is on a pull request targeting `master`.
2. Review schema changes and the generated EF migration. Do not edit the model
   snapshot without a corresponding migration.
3. Confirm every enabled paid provider and deployment has a positive budget
   price.
4. Confirm no secrets, local settings, development manifests, or credentials
   are part of the diff.
5. Require the build, CodeQL, and review checks to pass.
6. Resolve only review conversations that have been verified and addressed.
7. For extension releases, finish the backend deployment and production smoke
   test before uploading the extension ZIP.

Representative local checks are:

```bash
git diff --check
dotnet build Glosify.slnx --configuration Release
dotnet ef migrations has-pending-model-changes \
  --project Glosify/Glosify.csproj \
  --configuration Release \
  --no-build
dotnet test Glosify.Tests/Glosify.Tests.csproj \
  --configuration Release \
  --no-build \
  --no-restore
npm test --prefix Glosify.LiveSubtitles.Extension
npm test --prefix Glosify.ClientTests
```

CI remains authoritative because it also validates migrations against an empty
SQL Server and runs the packaged browser journeys.

## Standard release procedure

1. Merge the fully passing pull request into `master` without bypassing branch
   protection.
2. Locate the workflow run:

   ```bash
   gh run list \
     --branch master \
     --workflow "Build, migrate, and deploy Glosify" \
     --limit 5
   ```

3. Watch it to completion:

   ```bash
   gh run watch <run-id> --interval 10 --exit-status
   ```

4. Confirm `Apply reviewed database migrations` succeeds before
   `Deploy Azure Web App`.
5. Complete the production checks below. Do not upload a new extension package
   until its required backend version is healthy.

Do not use a local ZIP deployment or an ad hoc `az webapp deploy` for a normal
release; that would bypass the tested artifacts and migration-first ordering.

## Production verification

### Availability

```bash
curl --fail --show-error https://glosify-app.azurewebsites.net/healthz
curl --fail --show-error https://glosify-app.azurewebsites.net/readyz
```

Both should return `Healthy`. `/healthz` checks that the host is serving;
`/readyz` also checks Azure SQL. The serverless database may need more than a
minute to resume after being idle, so a brief readiness delay is not by itself
a failed deployment.

### Public and authentication surfaces

Verify these pages through the custom domain:

- `https://glosify.se/Home/Privacy`
- `https://glosify.se/Home/Terms`
- `https://glosify.se/Home/Support`
- `https://glosify.se/login`
- `https://glosify.se/Account/Register`

Confirm the legal pages show the configured public controller and contact
address. Login and registration must state that the one-time 25-credit trial
requires Google or Microsoft linking.

An unauthenticated request to the paid-service status endpoint must return
`401`, not paid-service state:

```bash
curl --silent --output /dev/null --write-out '%{http_code}\n' \
  https://glosify.se/api/service-status/paid-features
```

For a release affecting extension authentication, start from the installed
extension and confirm that Connect Glosify:

1. opens the Glosify login page;
2. preserves `redirect_uri`, `state`, and PKCE parameters through login or
   registration;
3. returns to the exact pinned `chromiumapp.org` callback; and
4. shows the correct credit balance after connection.

For a release affecting live subtitles, perform one short Enhanced session and,
when enabled, one Economical session on a regular HTTPS page. Confirm the
mode-specific audio disclosure is visible, subtitles start and stop, credits
are consumed at the displayed rate, transcript saving starts off, and an
opted-in transcript can be deleted.

## Failure handling and rollback

### Build failure

No production mutation has occurred. Fix the failure on a new commit or pull
request and let the complete workflow run again.

### Production migration failure

The workflow stops before deploying the web artifact, so the previous app
version remains active. Inspect the migration-bundle logs and the production
`__EFMigrationsHistory` state before retrying. Fix the migration in a reviewed
pull request; do not edit production schema or migration history by hand.

### Web deployment failure after a successful migration

The schema may now be ahead of the running application. Determine whether the
migration is backward-compatible before retrying or reverting application code.
Prefer a forward fix. Do not deploy an older package blindly.

### Application regression

Revert the offending merge through a pull request so the same checks and
deployment ordering apply. If the release included a schema migration, verify
that the previous application version works with the newer schema. Otherwise,
ship a forward application fix instead of rolling the schema backward.

### Data or destructive schema incident

Stop and preserve workflow logs and database evidence. Do not run an automatic
EF downgrade or manually alter `__EFMigrationsHistory`. An Azure SQL
point-in-time restore should restore to a separate database first and requires
an explicit, reviewed cutover plan.

### Paid-provider incident

The 300 SEK ledger is an application safety ceiling, not a real-time Azure
invoice cap. SQL and baseline Azure resources can continue to cost money after
paid features close, and Speech or ACS tokens issued before closure can have a
tail of up to one hour. Check Azure Cost Management separately when financial
exposure is suspected.

## Chrome Web Store release

After the compatible backend is deployed and verified:

```bash
npm test --prefix Glosify.LiveSubtitles.Extension
npm run package:store --prefix Glosify.LiveSubtitles.Extension
```

Upload only the generated ZIP under
`Glosify.LiveSubtitles.Extension/artifacts/package/`. The packager rebuilds and
validates the Store profile, requires `manifest.json` at the ZIP root, rejects
localhost and unexpected files, and checks the pinned extension identity and
assets.

Follow the complete
[Chrome Web Store submission guide](../Glosify.LiveSubtitles.Extension/store-listing/CHROME-WEB-STORE.md).
Use unlisted visibility and deferred publishing for the beta pilot. Put the
temporary reviewer account credentials only in the Store dashboard, never in
the repository or listing copy.

## Release record

Record the following in the pull request or release notes:

- merge commit and workflow run URL;
- migration name, or `none`;
- build and deploy conclusions;
- `/healthz` and `/readyz` results;
- legal/authentication smoke result when relevant;
- extension package filename and SHA-256 when relevant; and
- any production setting changed, by name only.
