# Microsoft Foundry generative AI

Glosify routes assistant conversations, typed vocabulary repair, and image text
extraction through the existing `glosify-speaking` Microsoft Foundry project.
Vocabulary repair and image extraction remain pinned to `gpt-5.4-mini`.
Assistant users can select `gpt-5.4-mini` or one of the configured xAI Grok
deployments. Speaking practice continues to use its existing versioned prompt
agents.

## Runtime architecture

Controllers and application services depend on `IGenerativeAiClient`.
`FoundryGenerativeAiClient` is the default implementation.
`GeminiGenerativeAiClient` is retained only for a temporary, deployment-wide
rollback.

The Foundry implementation uses a project-scoped `AIProjectClient` and a local
Agent Framework agent backed by the Foundry Responses API. It does not create a
persistent server-side assistant agent:

- SQL remains authoritative for saved assistant chats.
- `AssistantOrchestrator` owns the 24-model-turn limit and tool execution.
- Function calls and matching call IDs are persisted with tool results.
- Existing Gemini histories without call IDs receive deterministic replay-only
  IDs; Gemini thought signatures are ignored by Foundry.
- Mutation tools continue to produce pending changes that require user
  approval.
- Every model turn reserves and settles AI credits independently.

There is no semantic retry and no automatic Gemini fallback. Retrying an
ambiguous assistant response could repeat a charge, tool call, or queued
mutation.

## Configuration

Non-secret defaults are checked into `appsettings.json`:

```text
GenerativeAi__Provider=Foundry
GenerativeAi__Foundry__ProjectEndpoint=https://glosify-foundry.services.ai.azure.com/api/projects/glosify-speaking
GenerativeAi__Foundry__AssistantDeployment=gpt-5.4-mini
GenerativeAi__Foundry__StructuredDeployment=gpt-5.4-mini
GenerativeAi__Foundry__VisionDeployment=gpt-5.4-mini
GenerativeAi__Foundry__AllowedAssistantDeployments__0=gpt-5.4-mini
GenerativeAi__Foundry__AllowedAssistantDeployments__1=grok-4-1-fast-non-reasoning
GenerativeAi__Foundry__AllowedAssistantDeployments__2=grok-4-1-fast-reasoning
GenerativeAi__Foundry__TimeoutSeconds=180
```

`AssistantModels` in `appsettings.json` supplies display name, model publisher,
speed tier, cost tier, and credit multiplier for each allowlisted deployment:

| Deployment | Display tier | Credit multiplier |
|---|---|---:|
| `gpt-5.4-mini` | OpenAI, balanced, standard | 1x |
| `grok-4-1-fast-non-reasoning` | xAI, fast, standard | 1x |
| `grok-4-1-fast-reasoning` | xAI, thoughtful, premium | 2x |

The multipliers are Glosify product policy, not a representation of exact Azure
invoice ratios. They can be changed in configuration without changing the
database schema.

Startup validation rejects:

- a provider other than `Foundry` or explicit rollback value `Gemini`;
- a non-HTTPS or relative project endpoint;
- empty deployment names;
- a non-positive timeout;
- an assistant deployment absent from its allowlist;
- missing, duplicate, incomplete, or non-positive assistant model metadata; and
- a Gemini rollback without configured Gemini credentials.

The optional assistant `model` request property remains compatible with web and
mobile clients. It is resolved only against the configured allowlist. `Auto`
uses `gpt-5.4-mini`; the other entries display publisher, speed, cost tier, and
credit multiplier. With only the default deployment configured, the web UI
displays only `Auto`.

## Identity and RBAC

Local development uses `DefaultAzureCredential`, so authenticate with `az
login` or another supported developer credential. Non-development environments
use `ManagedIdentityCredential`. If a user-assigned managed identity is
attached, set its client ID in `AZURE_CLIENT_ID`; otherwise the system-assigned
identity is used.

The App Service identity requires the `Foundry User` role (formerly
`Azure AI User`; role ID `53ca6127-db72-4b80-b1b0-d745d6d5456d`) on the
`glosify-foundry` account/project. Foundry API keys must not be added to app
settings. Keep `Cognitive Services Speech User` on the Speech resource for the
speaking feature.

## Credits and errors

Each provider adapter reserves an estimate before its network call. Successful
Foundry calls commit the provider's actual input/output/total usage. Failure,
cancellation, refusal, or invalid structured output releases the reservation.
Assistant reservation and debit amounts apply the selected model's configured
credit multiplier. Usage rows record provider `foundry` and the actual
deployment used.

Existing controller routes and JSON response shapes are unchanged:

| Condition | HTTP status |
|---|---:|
| Invalid image media or model selection | 400 |
| Insufficient credits | 402 |
| Foundry throttling or temporary unavailability | 503 |
| Foundry timeout | 504 |
| Other Foundry or structured-response failure | 502 |

Messages exposed to clients are provider-neutral and do not include raw SDK or
transport details.

## Telemetry

`Glosify.GenerativeAi` activities and metrics include only:

- feature (`assistant`, `repair`, or `image_extraction`);
- provider and deployment;
- duration and outcome;
- input, output, and total tokens;
- assistant tool-call count;
- throttle, timeout, and upstream-failure counters; and
- credit reservation, commit, and release outcomes.

Prompts, transcripts, image bytes, function arguments, and generated
vocabulary are never added to logs or telemetry.

## Validation and live smoke tests

Normal validation:

```bash
dotnet build Glosify.slnx -c Release
dotnet test Glosify.slnx -c Release
```

The opt-in live suite is disabled unless explicitly enabled:

```bash
RUN_FOUNDRY_SMOKE_TESTS=true \
dotnet test Glosify.slnx -c Release \
  --filter Category=LiveFoundry
```

It covers one typed repair, one OCR fixture, one read-only function call, one
mutation function call that is returned but not executed, actual token usage,
telemetry emission, and the application-owned tool loop for every selectable
assistant deployment. Optional overrides are `FOUNDRY_PROJECT_ENDPOINT`,
`FOUNDRY_MODEL_DEPLOYMENT`, and comma-separated
`FOUNDRY_ASSISTANT_DEPLOYMENTS`.

## Rollback and removal

During the soak, rollback the whole deployment by setting:

```text
GenerativeAi__Provider=Gemini
```

Configure the rollback credential, restart/redeploy, and diagnose before
returning to Foundry. Do not migrate or delete user data.

Keep the rollback for at least seven consecutive days. Daily synthetic
coverage must exercise all three migrated paths, with no severity-1/2 incident,
duplicate or incorrect credit charge, or pending-change/tool-call regression.
AI errors may be at most one percentage point above the Gemini baseline, and
P95 latency at most 25% above it.

After the soak passes, delete the Gemini adapter, options, model factory, DI
branch, package, settings, tests, and terminology; rotate/revoke the Gemini key;
then repeat the Release build, full tests, repository-wide dependency search,
and live Foundry smoke suite.
