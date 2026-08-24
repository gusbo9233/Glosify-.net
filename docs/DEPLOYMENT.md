# Deployment runbook

Glosify deploys to Azure App Service from
`.github/workflows/master_glosify.yml`. Pull requests build and test; a reviewed
push to `master` creates and applies the EF migration bundle before deploying the
web application.

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
production. After the direct-OpenAI artifact is deployed, it removes the retired
Foundry/Gemini credentials, endpoints, deployments, speaking-agent pins, and
model-multiplier overrides. The cleanup deliberately runs after deployment so
the previous artifact remains functional during rollout.

Feature-specific settings remain required when those features are enabled:

- Azure Speech endpoint/resource/region settings for speaking transcription,
  pronunciation assessment, voices, and TTS;
- Azure Translator resource settings for Scribe subtitle mode;
- `RealtimeTranslation__ElevenLabs__ApiKey` for Scribe mode and optional saved
  source transcripts;
- Blob Storage, Azure Communication Services, Stripe, OAuth, and email settings
  for their corresponding features.

Managed identity is still used for supported Azure services such as Blob
Storage, Azure Speech, Translator, and telemetry. OpenAI uses its API key.

## Fixed AI configuration

The deployer cannot select generative models or providers through settings.
Application constants fix these routes:

| Feature | Route |
|---|---|
| Assistant, structured generation, image extraction, page translation, speaking text | OpenAI Responses API, `gpt-5.6-luna` |
| Enhanced live subtitles | OpenAI realtime translation, `gpt-realtime-translate` |
| Alternative subtitles | ElevenLabs `scribe_v2_realtime` + Azure Translator |
| Speaking audio | Azure Speech |

`GenerativeAi__TimeoutSeconds` may override the default 180-second timeout. Do
not add model names, alternate provider keys, agent pins, callback secrets, or
realtime endpoints to deployment configuration.

## Pricing and credit checks

`AiUsage__MonthlyBudget` fails closed for enabled, metered services without a
matching price. The shipped configuration prices:

- `gpt-5.6-luna`: 2.2373 SEK per million input tokens and 13.4233 SEK per million
  output tokens;
- `gpt-realtime-translate`: 0.3804 SEK per audio minute;
- `gpt-realtime-translate+elevenlabs-scribe-v2-realtime`: 0.7304 SEK per minute;
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

The workflow applies the reviewed bundle. This migration deliberately leaves the
historical assistant staging table in place; no destructive data migration is
part of the direct OpenAI change.

## Pre-deployment verification

```bash
dotnet test Glosify.slnx -c Release
npm test --prefix Glosify.ClientTests
npm test --prefix Glosify.LiveSubtitles.Extension
```

Run the browser suite when its dependencies are installed. Direct OpenAI smoke
tests are opt-in and require `RUN_OPENAI_SMOKE_TESTS=true`; they read the exact
`OPENAI_SECRET_KEY` from the environment or the Glosify user-secret store. Never
print the key in test output.

## Post-deployment checks

1. Confirm startup validation succeeds and the health endpoint is healthy.
2. Send a basic assistant turn and a tool-using turn; confirm usage rows show
   provider `openai`, model `gpt-5.6-luna`, and no hosted conversation ID.
3. Verify structured import, image extraction, and book-page translation.
4. Verify one speaking turn still uses Azure Speech for audio and Luna for text.
5. Start Enhanced subtitles and confirm relay readiness, translated captions,
   duration billing, reconnect, and graceful close.
6. Start Scribe mode and confirm Azure Translator output remains unchanged.
7. Review Application Insights errors, latency, throttling, token usage, and
   credit reservation/settlement counters.

## Rollback

Roll back the application artifact to the previous reviewed release. Do not add
an alternate provider/model setting as an emergency switch. If direct OpenAI is
unavailable, disable the affected feature through its existing feature flag or
restore the prior application release after reviewing data and schema
compatibility.
