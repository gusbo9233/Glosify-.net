# Glosify architecture

Glosify is a deliberately modular ASP.NET Core 10 MVC application in one web
project. Feature slices own their HTTP orchestration, application rules, EF Core
queries, views, and tests. The application does not add repository, unit-of-work,
CQRS, or mediator layers over EF Core.

## Runtime overview

```text
Browser / Chrome extension
          |
          v
ASP.NET Core MVC + APIs
          |
          +--> EF Core --> Azure SQL
          +--> OpenAI Responses API (gpt-5.6-luna)
          +--> OpenAI Realtime Translation (gpt-realtime-translate)
          +--> Azure Speech / Translator
          +--> ElevenLabs Scribe v2
          +--> Azure Blob Storage
          +--> Stripe
```

Identity and authorization are enforced at the application boundary. External
provider credentials stay on the server. API failures use the shared Problem
Details contract.

## Direct OpenAI generation

All generative text and image-input work is implemented by
`OpenAiGenerativeAiClient` on the official OpenAI C# SDK and Responses API. The
model is the code constant `OpenAiModels.Luna` (`gpt-5.6-luna`). There is no
provider selector, alternate model, model catalog, client-side model preference,
or deployment-configured fallback.

Every request:

- sets `store: false`;
- uses medium reasoning;
- sends a SHA-256 hash of the learner ID as the safety identifier;
- uses a 180-second timeout;
- reserves credits before transport and settles confirmed token usage afterward;
- maps throttling, upstream failures, timeouts, cancellation, and invalid output
  into the existing service exceptions and Problem Details behavior.

Glosify retains saved assistant conversation history and replays it manually;
OpenAI-hosted conversation state is not used. Structured generation supplies
strict JSON schemas. Image extraction sends
image input to the same fixed model.

## Assistant

The assistant is application-owned end to end:

- `AssistantProfileInstructions` contains four active profiles: the language quiz
  assistant, language librarian, Freestyle quiz assistant, and Freestyle librarian.
- `AssistantPromptBuilder` appends trusted, turn-specific quiz, language,
  document, transcript, and book context.
- `AssistantToolRegistry` and the per-tool classes define schemas and execute
  tools locally.
- `AssistantToolNarrowing` limits the offered surface based on the inferred
  intent and active context.
- `AssistantTurnRunner` preserves Responses function call IDs, executes local
  calls, and caps a run at 24 tool turns.

Read tools execute immediately. Mutating tools build application-side pending
changes; the user must apply or reject them. The historical pending-change
staging table remains mapped so existing rows are not destroyed, but it is not a
model callback or remote tool surface.

## Realtime subtitles

Enhanced subtitles use a server-side WebSocket relay to the fixed URL:

```text
wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate
```

The relay authenticates with `OPENAI_SECRET_KEY`, sends the hashed safety
identifier, uses the existing single-use relay-token authorization and duration
billing, and sends `session.close` during graceful shutdown. The extension never
receives the OpenAI key. Its existing message protocol and reconnect behavior are
preserved.

Scribe mode remains the user-selectable alternative: ElevenLabs Scribe v2
produces evolving source text and Cloudflare M2M100 translates it. Enhanced mode
can also send audio to Scribe when the user separately enables saved source
transcripts. Glosify does not store tab audio.

## Data and accounting

SQL Server stores Identity data, quizzes, books, chats, transcripts, credit
reservations, usage debits, and provider usage. New text and vision usage rows
use provider `openai` and model `gpt-5.6-luna`; enhanced subtitle rows
use provider `openai` and model `gpt-realtime-translate`. Historical provider
values, including rows from retired features, remain valid database history.

The checked-in budget prices include the reviewed SEK conversion and 15% safety
markup:

| Meter | Price |
|---|---:|
| Luna input | 2.2373 SEK / million tokens |
| Luna output | 13.4233 SEK / million tokens |
| Realtime translation | 0.3804 SEK / audio minute |
| Realtime translation + saved Scribe transcript | 0.4531 SEK / minute |
| Scribe + Cloudflare estimate | 0.35 SEK / minute |

## Security and privacy boundaries

- `OPENAI_SECRET_KEY`, Speech, Cloudflare, Scribe, Stripe, and storage
  credentials are server-only.
- Controllers enforce ownership before loading or changing learner data.
- Assistant tool results and learner content are treated as untrusted data, not
  instructions.
- Antiforgery, authorization policies, input limits, rate limits, and the shared
  Problem Details mapping stay at the HTTP boundary.
- Telemetry records identifiers, classifications, timings, and usage according
  to feature policy; provider safety identifiers are one-way hashes.

## Repository map

```text
Glosify/                         MVC app, feature services, EF migrations
Glosify.Tests/                   .NET behavior and contract tests
Glosify.BrowserTests/            Chromium end-to-end tests
Glosify.ClientTests/             browser JavaScript tests
Glosify.LiveSubtitles.Extension/ Chrome extension and tests
docs/                            ADRs, operations, analytics, screenshots
scripts/                         local development and operations helpers
```

ADRs in `docs/adr/` record decisions at the time they were made. Current code,
tests, migrations, CI, and this document are authoritative for the active
runtime.
