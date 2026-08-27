# Deployment runbook

Glosify deploys to Azure App Service from
`.github/workflows/master_glosify.yml`. Pull requests targeting `master` build
and test. The repository ruleset should automatically request GitHub Copilot
code review for new pull requests and new pushes; draft reviews remain disabled
to avoid feedback on unfinished work.

Copilot review is advisory: its comments do not count as an approval, replace
CI, or establish that a finding is correct. Validate its findings against the
current code, tests, migrations, configuration, and applicable primary
documentation. A reviewed push to `master` applies a compatibility migration,
deploys and readiness-checks the replacement application, then applies the
destructive EF migration and checks readiness again.

## Required App Service settings

Set secrets as App Service settings or Key Vault references. Do not commit them
to configuration files.

| Setting | Purpose |
|---|---|
| `ConnectionStrings__DefaultConnection` | Azure SQL connection string |
| `OPENAI_SECRET_KEY` | Direct OpenAI Responses and realtime translation |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Application Insights export |

`OPENAI_SECRET_KEY` is mandatory outside Development and Testing. The app reads
that exact environment variable explicitly. It is never emitted in HTML, API
responses, relay tokens, logs, or extension configuration.

The deployment workflow verifies that the setting is non-empty before changing
production. After the replacement artifact is deployed, it removes retired
Foundry/Gemini, Speaking, and Azure Communication Services settings. The cleanup
deliberately runs after deployment so the previous artifact remains functional
during rollout.

Feature-specific settings remain required when those features are enabled:

- Azure Speech endpoint/resource/region settings for server-side TTS;
- Azure Translator resource settings for Scribe subtitle mode;
- `RealtimeTranslation__ElevenLabs__ApiKey` for Scribe mode and optional saved
  source transcripts;
- Blob Storage, Stripe, OAuth, and email settings
  for their corresponding features.

Managed identity is still used for supported Azure services such as Blob
Storage, Azure Speech, Translator, and telemetry. OpenAI uses its API key.

Scribe translates the latest partial after one second and then at most every
two seconds, with eight Unicode characters of accumulated growth. These
non-secret App Service settings can tune or disable that behavior without a
deployment:

| Setting | Default | Purpose |
|---|---:|---|
| `RealtimeTranslation__ElevenLabs__TranslatePartials` | `true` | Set to `false` to translate committed transcripts only. |
| `RealtimeTranslation__ElevenLabs__PartialInitialDelaySeconds` | `1` | Delay before the first partial translation. |
| `RealtimeTranslation__ElevenLabs__PartialIntervalSeconds` | `2` | Minimum interval between partial translations. |
| `RealtimeTranslation__ElevenLabs__PartialMinimumGrowthCharacters` | `8` | Accumulated Unicode growth required for a partial update. |

For a temporary legacy-like cadence, use an initial delay of `0`, an interval
of `0.75`, and minimum growth of `1`. Prefer adjusting only the initial delay
for latency tuning and the recurring interval for cost tuning.

### Administrator Scribe capture

Scribe sessions started by an account listed in `Admin__Emails` automatically
store an internal analysis trace in `RealtimeTranslationCaptureEvents`. This is
separate from the user-facing saved-transcript feature and does not capture
ordinary accounts. Each trace contains the Scribe source partials and finals,
every Azure Translator partial or final result, whether that result required a
provider request, and every bubble finalized by the server. Caption text is
stored only in this database table; logs and metrics remain text-free.

Configure each administrator email as an indexed App Service setting, for
example `Admin__Emails__0=admin@example.com`. After completing a Scribe run,
open `/Admin/TranslationCaptures` while signed in as that administrator to
download the latest run as JSON. Pass a captured `sessionId` query parameter to
download an older run. The endpoint never returns another user's sessions.

The server is authoritative for Scribe bubble boundaries. Relay events retain
their existing `text` field for older extension builds and add
`committedBubbles` plus `pendingText` for extension builds that render the
server decision directly.

## Fixed AI configuration

The deployer cannot select generative models or providers through settings.
Application constants fix these routes:

| Feature | Route |
|---|---|
| Assistant, structured generation, image extraction, page translation | OpenAI Responses API, `gpt-5.6-luna` |
| Enhanced live subtitles | OpenAI realtime translation, `gpt-realtime-translate` |
| Alternative subtitles | ElevenLabs `scribe_v2_realtime` + Azure Translator |
| Book text-to-speech | Azure Speech |

`GenerativeAi__TimeoutSeconds` may override the default 180-second timeout. Do
not add model names, alternate provider keys, agent pins, callback secrets, or
realtime endpoints to deployment configuration.

## Pricing and credit checks

`AiUsage__MonthlyBudget` fails closed for enabled, metered services without a
matching price. The shipped configuration prices:

- `gpt-5.6-luna`: 2.2373 SEK per million input tokens and 13.4233 SEK per million
  output tokens;
- `gpt-realtime-translate`: 0.3804 SEK per audio minute;
- `gpt-realtime-translate+elevenlabs-scribe-v2-realtime`: 0.4531 SEK per minute;
- `elevenlabs-scribe-v2-realtime+azure-translator-nmt`: 0.35 SEK per minute.

Before enabling realtime translation, verify the relevant provider is budgeted,
the duration meter has `AudioSekPerMinute`, extension redirect URIs are approved,
and the relay startup/heartbeat/session limits match the production policy.

## Database deployment

The application does not migrate its schema at startup. Generate and review a
migration locally, then verify:

```bash
dotnet ef migrations has-pending-model-changes --project Glosify
dotnet ef migrations bundle --project Glosify --configuration Release
```

The workflow first targets migration
`20260827093000_PrepareClassroomRetirement`, which detaches foreign keys from
retired tables into retained Identity, quiz, and book tables without deleting
data. Both the previous and replacement artifacts remain functional on that
compatibility schema. After the replacement artifact passes `/readyz`, the
workflow applies migration `20260827095613_RemoveSpeakingAndClassrooms`. That
migration is intentionally destructive: it drops `AcsUserIdentities`, all
`Classroom*` tables, and only the retired `QuizAttempts.ClassroomId` foreign key,
index, and column. It preserves `QuizAttempts`, `QuizAttemptItems`, Identity,
quizzes, books, assistant data, credits, and provider-usage history. Historical
migrations must remain unchanged.

The workflow records `classroomRetirementPrepared=true` and
`classroomRetirementComplete=true` as tags on the Azure SQL database resource.
The prepared tag prevents a rerun or later deployment from targeting the
compatibility migration after the destructive migration has already completed.

### Required retirement BACPAC

Immediately before merging or deploying the retirement migration:

1. Confirm the remote `foundry-version` branch is available and no administrator
   is using the retired features.
2. Export the full production Azure SQL database to a BACPAC.
3. Store it encrypted in private, operator-controlled Azure storage with public
   access disabled. Verify that the object exists and has a non-zero size.
4. Record the database name, UTC export timestamp, blob URI, size, and deletion
   date in the private operations record—not in this repository.
5. Set a 90-day deletion date or lifecycle rule and verify deletion after that
   date.

Do not deploy the migration until the export has completed successfully. A
migration rollback recreates only empty retired tables and the nullable attempt
column; it cannot restore classroom or ACS identity rows. Restoring those rows
requires the BACPAC and the archived `foundry-version` code.

## Pre-deployment verification

```bash
dotnet test Glosify.slnx -c Release
npm test --prefix Glosify.ClientTests
npm test --prefix Glosify.LiveSubtitles.Extension
dotnet ef migrations has-pending-model-changes --project Glosify
dotnet ef migrations bundle --project Glosify --configuration Release
dotnet publish Glosify/Glosify.csproj -c Release
```

Run the browser suite when its dependencies are installed. Direct OpenAI smoke
tests are opt-in and require `RUN_OPENAI_SMOKE_TESTS=true`; they read the exact
`OPENAI_SECRET_KEY` from the environment or the Glosify user-secret store. Never
print the key in test output.

## Post-deployment checks

1. Confirm startup validation succeeds and the health endpoint is healthy.
2. Send a basic assistant turn and a tool-using turn; confirm usage rows show
   provider `openai`, model `gpt-5.6-luna`, and no hosted conversation ID.
3. Verify quiz practice, custom quizzes, Anki review, Books/TTS, structured
   import, image extraction, and book-page translation.
4. Start Enhanced subtitles and confirm relay readiness, translated captions,
   duration billing, reconnect, and graceful close.
5. Start Scribe mode and confirm Azure Translator output remains unchanged.
6. Verify authentication, payments, and saved assistant/transcript flows.
7. Confirm `/Speaking`, `/api/speaking/*`, `/Classroom/*`, and
   `/hubs/classroom-chat` return 404 for an authenticated administrator.
8. Review Application Insights for startup, routing, missing-service, and SQL
   failures, plus latency, throttling, token usage, and
   credit reservation/settlement counters.
9. Confirm the post-deployment settings cleanup completed, then repeat health
   and smoke checks.

## Rollback

Do not roll back the application artifact across this migration without first
reviewing schema compatibility. Rolling the migration down recreates empty
retired schema only. Restoring the old implementation with its production data
requires the archived `foundry-version` code and a restore/import from the
private BACPAC. For unrelated application failures, restore the previous
schema-compatible reviewed artifact; do not add an alternate provider/model
setting as an emergency switch.
