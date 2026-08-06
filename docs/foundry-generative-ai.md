# Microsoft Foundry generative AI

Glosify routes assistant conversations, typed vocabulary repair, image text
extraction, and page translation through the `glosify-assistant` Microsoft Foundry
project. Vocabulary repair, image extraction, and page translation are pinned to
`gpt-5.6-luna`. Assistant users can select `gpt-5.6-luna` or `grok-4.3`. Speaking
practice runs in its own project, `glosify-speaking`, on its existing versioned
prompt agents.

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
GenerativeAi__Foundry__ProjectEndpoint=https://glosify-assistant-resource.services.ai.azure.com/api/projects/glosify-assistant
GenerativeAi__Foundry__AssistantDeployment=gpt-5.6-luna
GenerativeAi__Foundry__StructuredDeployment=gpt-5.6-luna
GenerativeAi__Foundry__VisionDeployment=gpt-5.6-luna
GenerativeAi__Foundry__PageTranslationDeployment=gpt-5.6-luna
GenerativeAi__Foundry__PageTranslationFallbackDeployment=DeepSeek-V4-Flash
GenerativeAi__Foundry__AllowedAssistantDeployments__0=gpt-5.6-luna
GenerativeAi__Foundry__AllowedAssistantDeployments__1=grok-4.3
GenerativeAi__Foundry__TimeoutSeconds=180
```

`AssistantModels` in `appsettings.json` supplies display name, model publisher,
speed tier, cost tier, and credit multiplier for each allowlisted deployment:

| Deployment | Display tier | Credit multiplier |
|---|---|---:|
| `gpt-5.6-luna` | OpenAI, balanced, standard | 1x |
| `grok-4.3` | xAI, thoughtful, premium | 2x |

Every deployment named here must also carry a price under
`AiUsage:MonthlyBudget:Models`. `GenerativeAiOptionsValidator` enforces that at
startup, because the credit reservation that precedes each call fails closed on an
unpriced deployment — without the check, repointing the project turns every
assistant request into a 500. `SpeakingOptionsValidator` applies the same rule to
`Speaking:ModelDeployment`.

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

## Assistant agent profiles

Each assistant turn is routed to a profile, chosen in `AssistantOrchestrator`
from the request context — never by asking a model to pick. A profile fixes two
things: which tools are offered, and where the instructions come from.

| Profile | Selected when | Agent | Tools |
|---|---|---|---|
| `Librarian` | no quiz selected | `glosify-librarian` | 23 |
| `QuizAssistant` | a quiz is selected | `glosify-quiz-assistant` | 32 |
| `CustomQuizBuilder` | a custom quiz is open in the creator | `glosify-quiz-builder` | 14 |
| `General` | no agent configured for the profile | — | in-code declarations |

Each profile's context block carries the per-turn facts alone. That includes the book
page and saved transcript text, because an authored agent receives only this block —
omitting them would silently break "add words from this page".

The builder profile exists because the general tool surface let the model call
`create_custom_quiz` while the user was looking at an open quiz, forking a new
empty quiz instead of filling in the one on screen. The creation tools are not
on the builder profile at all, so that outcome is unreachable rather than
defended against.

Only instructions and tool scoping move to Foundry. Tool handlers, the
propose-and-Apply pending change flow, chat history in SQL, the model-turn loop,
and credit metering all stay in this application, because the tools execute
against the request's `AgentToolContext` and the user's database rows.

### Projects

Assistant work runs in its own resource and project, separate from speaking:

| Purpose | Resource | Project | Region |
|---|---|---|---|
| Assistant, repair, vision, page translation | `glosify-assistant-resource` | `glosify-assistant` | swedencentral |
| Speaking practice | `glosify-foundry` | `glosify-speaking` | eastus |

The assistant project holds three deployments — `gpt-5.6-luna` and `DeepSeek-V4-Flash`
(DataZoneStandard) and `grok-4.3` (GlobalStandard) — all token-billed pay as you go,
no provisioned throughput. `gpt-5.6-luna` and `grok-4.3` are allowlisted for the
assistant model menu; `DeepSeek-V4-Flash` is the page-translation fallback only.

Both the App Service managed identity and developer accounts need `Foundry User` on
`glosify-assistant-resource`, exactly as they already have on `glosify-foundry`. The
App Service system-assigned identity holds it as of 2026-08-06; a new environment
needs the same grant, and its absence shows up as a warning that the agent could not
be read followed by 502s rather than as anything obviously permission-shaped.

### Authoring the agent

Agents live in the `glosify-assistant` project. Three are published, one per profile:
`glosify-quiz-builder`, `glosify-quiz-assistant`, and `glosify-librarian`. All three
are wired into code.

```text
GenerativeAi__Foundry__Agents__CustomQuizBuilder__Name=glosify-quiz-builder
GenerativeAi__Foundry__Agents__CustomQuizBuilder__Version=3
GenerativeAi__Foundry__Agents__QuizAssistant__Name=glosify-quiz-assistant
GenerativeAi__Foundry__Agents__QuizAssistant__Version=3
GenerativeAi__Foundry__Agents__Librarian__Name=glosify-librarian
GenerativeAi__Foundry__Agents__Librarian__Version=3
```

Run `tools/foundry/export-agents.sh` after publishing a new version and bumping the
configuration. It writes every configured agent version — assistant and speaking
both — to `.foundry/agents/`, so the instructions and tool declarations the models
actually run on have a diffable record here. It is an export only: editing those
files changes nothing.

Leaving either value empty — the checked-in default — runs the profile on the
in-code instructions. A missing, unreachable, or non-prompt agent logs a warning
and falls back the same way, so publishing a broken version degrades the
assistant's wording rather than breaking the feature. Definitions are cached per
`name@version` for the process lifetime: publish a new version and bump the
config, rather than editing a version in place.

An agent may name a model. That is treated as a default only: a user's model
selection wins, and a model outside `AllowedAssistantDeployments` is ignored
with a warning, so portal edits cannot route traffic to an unapproved
deployment.

The agent carries only the static rules. Per-turn facts (backing quiz, open
custom quiz, languages) are appended by
`AssistantOrchestrator.BuildCustomQuizBuilderContext`, so instructions must not
restate them. Starting text for the agent:

```text
You are Glosify's custom quiz builder. The user is working inside the custom
quiz creator, looking at one open custom quiz. Every request targets that
document. The element tools already default to it, so omit custom_quiz_id.

How tools work:
- get_custom_quiz and list_custom_quiz_templates, list_words, and search_words
  execute immediately and return their results to you.
- The element tools propose changes that are queued for the user to review and
  Apply. You do not call any commit tool. Because the user reviews everything,
  propose changes freely when they seem helpful.
- Inspect the open document with get_custom_quiz before configuring or removing
  elements. Add one element per call; never send a blocks array or a complete
  document.
- Word bindings may only reference words already in the backing quiz. Use
  list_words or search_words to find them, and expected_text for literal answers
  such as verb endings.

Composition:
- A playable document needs exactly one submit_button, exactly one
  feedback_message, and at least one answer control.
- Every answer control needs a specific learner-visible label containing its
  question or gap, and multiple answer controls need distinct labels.
- Text inputs need either an expected word binding or literal expected_text;
  choice controls need at least two options and valid correct selections.
- Use stable descriptive element ids and non-overlapping 12-column layout
  coordinates.

Layout:
- Prefer compact textbook exercise patterns: a short heading and instruction
  followed by consecutive rows, with minimal card chrome.
- A single-line text_input is a compact inline blank. Put {{blank}} in the label
  exactly where the input belongs, for example "1. ja bed{{blank}} jutro w
  domu." Never include underscore or dot runs: they create a fake blank beside
  the real control.
- For conjugation, cloze, and word transformation, use one text_input per
  compact row and do not add a separate prompt_label for the same item.
- For fill-in-the-ending questions, set expected_text to only the literal ending
  (for example "e" or "esz"), not the full word unless the user asks for it.

Style:
- Match your response to the request: a short confirmation when you queued
  changes, a fuller answer when the user asks a question.
- Do not mention internal tool names, tool calls, ids, JSON, or implementation
  details.
```

## Agents own their tools

An agent declares its tools in Foundry as `function` entries on the prompt agent, the
same way the speaking agents do. Glosify executes them: `AssistantTools.ExecuteAsync`
dispatches by name against the calling user's rows. So Foundry owns *which* tools exist
and how they are described to the model, and this application owns what they do.

`FoundryAgentInvoker` reads those declarations from the agent version and
`FoundryGenerativeAiClient` offers them to the model in place of the in-code list. An
agent that declares no tools falls back to the in-code declarations, so an unconfigured
or unreachable agent still works.

Publish tool declarations from the code rather than hand-writing them in the portal, so
schemas cannot drift from the handlers that receive them. `glosify-quiz-builder` v3
carries the 14 quiz-builder declarations, generated from
`AssistantTools.CustomQuizBuilderDeclarations`.

A tool name the running build has no handler for comes back to the model as an unknown
tool rather than silently doing nothing, which is the failure mode to expect if someone
adds a tool in the portal ahead of the code.

## Calling tools over MCP (not adopted)

An earlier plan exposed the tools as an MCP server for the agent to call back into.
Two API constraints ruled it out for this application:

- `tools` cannot be passed on a request that uses `agent_reference`, so the per-request
  MCP `server_url` that was going to carry a signed per-user session is not possible
  alongside an agent reference.
- Declaring the MCP tool on the agent instead needs a static URL, which cannot identify
  the calling user. Foundry's per-user mechanisms (`x-ms-user-identity`, isolation keys)
  scope Foundry-side session state and are not forwarded to an MCP server, and OAuth
  identity passthrough would require Glosify to become an OAuth2 authorization server.

Declaring an MCP tool whose server is unreachable also fails *every* response with
`external_connector_error` 424 rather than degrading, so the tool must never be attached
before the endpoint is live.

The MCP endpoint below still exists and works. It is the path to revisit if tool
execution should ever genuinely leave this application.

### Why the acting user travels in the URL

Foundry's MCP authentication preserves end-user context in exactly one mode, OAuth
identity passthrough, and its Entra variant requires the user's tenant to match the
Foundry project's tenant. Glosify authenticates users with ASP.NET Core Identity against
its own store (`AddDefaultIdentity<ApplicationUser>`), with Google and Microsoft only as
external logins into local accounts. Those users have no Entra identity, so passthrough
would require Glosify to become an OAuth2 authorization server.

Instead, key-based auth carries a shared credential and Glosify mints the user context
itself: each response is created with an MCP tool whose `server_url` embeds a signed,
short-lived session naming the acting user and their page context.

```text
POST {project_endpoint}/responses
  agent_reference: glosify-quiz-builder
  tools: [{ type: mcp,
            server_url: https://glosify.../assistant/mcp/s/<session-token>,
            project_connection_id: glosify-mcp-shared-key,
            require_approval: never }]
```

`AssistantMcpSessionCodec` signs the session with HMAC-SHA256 and verifies it in fixed
time. The token is the real authenticator, so it is deliberately short-lived (30 minutes
by default): it travels in a URL, and URLs reach logs.

```text
Assistant__Mcp__SigningKey=<32+ characters, from Key Vault>
Assistant__Mcp__SharedSecret=<optional header credential Foundry sends>
Assistant__Mcp__SessionLifetimeMinutes=30
```

Without a signing key the route returns 404 and nothing is exposed. With one, the
endpoint filter rejects a wrong shared secret or an invalid, tampered, or expired
session before the protocol handshake.

### Status

Landed: the MCP endpoint at `/assistant/mcp/s/{token}`, session minting and
verification, dispatch into the existing tool handlers, and the full quiz-builder tool
set including the mutating element tools.

A change queued by a Foundry-run tool call has no orchestrator turn to collect it, so it
is written to `assistant_pending_changes` keyed by the session's conversation id. Each
call also primes its `AgentToolContext` from that store, which keeps the checks that read
what a turn already queued — duplicate answer labels, for one — working across separate
calls.

Still to do:

1. Create responses through `agent_reference` with the per-request MCP tool, replacing
   the in-process tool loop for the quiz-builder profile. Note that `agent_reference`
   pins the model: a request that overrides it fails with "Model must match the agent's
   model". The plan is a hybrid — the default model runs through `agent_reference`, and a
   user-selected alternative runs the local loop on the agent's instructions — rather
   than publishing one agent version per model.
2. Move credit metering from per-model-turn to per-response, since Foundry runs the loop
   internally and Glosify no longer sees each turn.
3. Move chat history to Foundry conversations and repoint the chats UI at them.
4. Read pending changes from the store in the Apply path, so a Foundry-driven
   conversation can be applied. Today only the in-process loop's changes, written onto
   the assistant message, are applied.

Local development needs a public tunnel: Agent Service only reaches remote endpoints, so
`localhost` cannot serve MCP.

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

Foundry usage also shares one application-wide monthly monetary budget. The
default `AiUsage:MonthlyBudget:LimitSek` is `200`; the period rolls over on the
first day of each month in `Europe/Stockholm`. Estimated calls reserve against
the shared SQL ledger before reaching Foundry, successful calls reconcile the
reservation against reported usage, and failed calls release it. Costs are
stored as integer micro-SEK to avoid floating-point and per-request öre rounding.

Input and output prices are configured per deployment under
`AiUsage:MonthlyBudget:Models`. The `gpt-5.4-mini` and Grok 4.1 rates correspond to
the deployed East US SKUs on 2026-07-19: Data Zone Standard for `gpt-5.4-mini` and
Global Standard for the two Grok 4.1 deployments. Of those, only
`grok-4-1-fast-non-reasoning` is still routed to, by `Speaking:ModelDeployment`.

> **The `glosify-assistant` rates are provisional.** `gpt-5.6-luna`, `grok-4.3`, and
> `DeepSeek-V4-Flash` currently carry 2x the `gpt-5.4-mini` rates rather than prices
> sourced from Azure retail pricing, which does not publish meters for them. They are
> deliberately high so the monthly budget errs toward stopping early instead of
> overspending; the visible symptom of leaving them wrong is a premature 503. Replace
> them with the real per-SKU rates for swedencentral.

Recheck Azure retail pricing when a deployment, SKU, region, or price changes. A
budgeted provider with no matching deployment price fails closed instead of making an
unaccounted request — and since 2026-08-06, startup validation rejects that
configuration outright rather than letting each request discover it.

Existing controller routes and JSON response shapes are unchanged:

| Condition | HTTP status |
|---|---:|
| Invalid image media or model selection | 400 |
| Insufficient credits | 402 |
| Application monthly budget exhausted | 503 |
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
