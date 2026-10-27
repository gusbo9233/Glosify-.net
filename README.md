# GlobeGlotter

[![Build, test, and deploy](https://github.com/gusbo9233/Glosify-.net/actions/workflows/master_glosify.yml/badge.svg)](https://github.com/gusbo9233/Glosify-.net/actions/workflows/master_glosify.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**[Live app](https://glosify.se)** · [Case study](docs/portfolio-case-study.md) · [Architecture](docs/ARCHITECTURE.md) · [ADRs](docs/adr/) · [Tests](Glosify.Tests/)

GlobeGlotter is an ASP.NET Core 10 MVC language-learning application with quizzes,
FSRS-6 study collections, books, saved assistant chats, a focused text-translator
Chrome extension, and a separate extension for live translated subtitles.

Explore and public library sharing were retired on October 17, 2026. See
[the decision and recovery branch](docs/adr/0003-retire-explore.md).

## Guest browsing

Visitors can browse Books, Quizzes, Anki, Transcripts, and Translations without
signing in. Each section offers a bundled starter sample matching the selected
learning language, rendered in the same library cards, readers, quiz workspace,
and Anki study UI as personal content. `/preview/{section}/sample` pages use
bundled view models and one-page PDFs, never a shared account or public library
records. Old `/spanish` links remain compatible and follow the current selection.
Sample typing and flashcard practice runs only in browser memory and resets on
refresh.

Creating content, using the assistant, and paid speech require sign-in through
a dismissible dialog. The standalone `/login` page remains available. Signing
in returns to the corresponding personal library without copying samples or
transferring guest results. Samples follow the language preference across
libraries, detail pages, practice, speech labels, and PDF readers. The guest
cookie takes precedence over the older account cookie. A guest's explicit choice
becomes the saved account language after successful sign-in to a personal account;
otherwise the saved account language is restored. Signed-in pages and the assistant
use that saved preference, so an older browser cookie cannot override it. All 69 learning languages
are covered, with English meanings; English samples use Spanish meanings.
Freestyle has a solar-system starter. With no selection, samples default to Spanish.

Each accepted assistant message captures the selected learning language for its
prompt, tools, and transcript context, including queued work and worker restarts.
The web panel sends the language displayed on its page; stale pages and follow-ups
after a language change are rejected with a reload/switch-back message. Explicit
quiz or Anki context must match that selection; an older implicit Anki context
cannot override it. Changing language starts a separate chat, while already
accepted work retains the language it was sent with.
Clearing the selection opens a chat with no learning language; earlier chats
become available again when their language is selected.

Starter phrases live in `Glosify/Services/Guest/starter-content.json` and are
embedded in the application. Each language has five practice phrases and a short
greeting dialogue. After editing the content, regenerate the one-page PDFs with
`node scripts/build-guest-books.mjs` (uses the existing LiveSubtitles extension
Playwright dependency and locally installed Chrome). Generated PDFs are bundled
under `Glosify/wwwroot/samples/starter-*.pdf`; no translation provider or database
is needed to browse them.

## AI services

All generative, text-agent, and vision work goes directly from the GlobeGlotter server
to the OpenAI Responses API. The model is fixed in code to `gpt-6-luna`; there
is no model picker, configured alternative, or provider fallback. Prompts, JSON
schemas, and function tools are defined and executed in this repository. GlobeGlotter
replays its own saved history and every Responses request uses `store: false`.

ElevenLabs v3 provides server-side text-to-speech for book reading, with a bounded memory cache. The Enhanced
subtitle relay connects server-side to `gpt-realtime-translate` while
the translated Scribe alternative uses ElevenLabs Scribe v2 followed by Cloudflare
Workers AI's M2M100 translator. Original captions use Scribe alone and return its
partial and finalized transcription without calling a translation provider.
Provider keys never reach the browser or extension.

The production key is the Azure App Service setting `OPENAI_SECRET_KEY`. For
local AI work, set the same name with user secrets. No OpenAI key belongs in an
appsettings file, browser storage, or the extension:

```bash
dotnet user-secrets set "OPENAI_SECRET_KEY" "<key>" --project Glosify
```

## Design and stack

The repository deliberately keeps one web project. MVC and API controllers
orchestrate HTTP work, feature services own application rules, and EF Core talks
directly to SQL Server. There is no generic repository, CQRS layer, or separate
domain assembly.

Main technologies include .NET 10, EF Core 10, Azure SQL, ASP.NET Core Identity,
OpenAI, Cloudflare Workers AI, Azure Blob Storage, ElevenLabs v3/Scribe v2,
Stripe, OpenTelemetry,
xUnit, AngleSharp, and Playwright.

```text
Glosify/                         Web app and EF migrations
Glosify.Tests/                   .NET unit, integration, and contract tests
Glosify.BrowserTests/            Chromium user journeys
Glosify.ClientTests/             Browser JavaScript tests
Glosify.LiveSubtitles.Extension/ Chrome extension and tests
Glosify.Translator.Extension/    Text-translator extension and tests
docs/                            Guides, ADRs, and screenshots
scripts/                         Development and operations helpers
```

## Local setup

You need the .NET 10 SDK, Node.js 24, and Docker or another container runtime.
Install web assets before building: `npm ci --prefix Glosify/Frontend`.
See [frontend ownership and build commands](Glosify/Frontend/README.md). Development
uses the local SQL Server container rather than Azure SQL.

```bash
dotnet tool restore
docker compose -f docker-compose.dev.yml up -d

dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Server=localhost,1433;Database=glosifydb;User Id=sa;Password=Local_Dev_Only_1;Encrypt=True;TrustServerCertificate=True;" \
  --project Glosify

./scripts/dev-db-reset.sh
dotnet dev-certs https --trust
dotnet run --project Glosify --launch-profile https
```

In Development, register with an email and password at
`https://localhost:7032/Account/Register`; the login route is `/login`.
The password signup form and `/api/auth/register` are enabled only in Development;
other environments require Google or Microsoft for signup. Ordinary quiz, Anki,
sharing, and UI development does not
need external service credentials. Azure-backed features use `az login` during
local development.

### Test direct OpenAI locally

The HTTPS launch profile enables Enhanced subtitles and allowlists the pinned
development extension callback. It still reads the exact `OPENAI_SECRET_KEY`
from the GlobeGlotter user-secret store.

Run the two opt-in direct API smoke tests (structured output plus a stateless
two-request function loop) with:

```bash
RUN_OPENAI_SMOKE_TESTS=true dotnet test Glosify.Tests \
  --filter FullyQualifiedName~OpenAiLiveSmokeTests
```

For the extension, build and load
`Glosify.LiveSubtitles.Extension/artifacts/development` as an unpacked Chrome
extension, then sign in and select Enhanced:

```bash
npm run build:dev --prefix Glosify.LiveSubtitles.Extension
```

The independent text-translator extension is built and loaded separately. It
uses its own pinned development ID, opens one movable/resizable translator per
active tab, and stores only authentication material, language choices, and the
optional preference field in trusted extension storage:

```bash
npm run build:dev --prefix Glosify.Translator.Extension
```

The production Chrome Web Store ID is assigned when the draft is first uploaded. Before
publishing, add its exact callback
`https://<store-id>.chromiumapp.org/glosify` to the production
`ExtensionAuth:AllowedRedirectUris` list. Publication is not performed by this
repository workflow.

Chrome must trust the local ASP.NET Core HTTPS certificate. Scribe mode also
requires ElevenLabs and the protected Cloudflare Worker configuration.

The application never changes the schema at startup. Use:

```bash
dotnet ef database update --project Glosify
dotnet ef migrations has-pending-model-changes --project Glosify
```

## Tests

Use .NET 10 and Node.js 24. Install the locked JavaScript dependencies before
running any npm check:

```bash
npm ci --prefix Glosify/Frontend
npm ci --prefix Glosify.ClientTests
npm ci --prefix Glosify.LiveSubtitles.Extension
npm ci --prefix Glosify.Translator.Extension

dotnet test Glosify.slnx -c Release
python3 -m unittest discover -s scripts -p 'test_*.py'
npm run lint --prefix Glosify/Frontend
npm run typecheck --prefix Glosify/Frontend
npm test --prefix Glosify.ClientTests
npm test --prefix Glosify.LiveSubtitles.Extension
npm run lint --prefix Glosify.LiveSubtitles.Extension
npm run test:browser --prefix Glosify.LiveSubtitles.Extension
npm test --prefix Glosify.Translator.Extension
npm run lint --prefix Glosify.Translator.Extension
npm run test:browser --prefix Glosify.Translator.Extension
```

The extension browser command runs both synthetic lifecycle journeys and the
real Chrome tab-capture profile. Its real-capture gate requires Linux/X11 and
`xdotool`; non-Linux local runs report that single journey as skipped.

Without their prerequisites, SQL Server, application-browser, and direct OpenAI
tests are reported as skipped. Enable the isolated SQL Server integration test
only against an explicitly configured non-production server:

```bash
RUN_SQLSERVER_TESTS=true \
ConnectionStrings__DefaultConnection='Server=127.0.0.1,1433;Database=glosify-tests;User Id=sa;Password=...;Encrypt=True;TrustServerCertificate=True' \
dotnet test Glosify.Tests -c Release --filter FullyQualifiedName~SqlServer
```

Application journeys run against published output in the `BrowserTesting` host
environment. The reusable launcher requires an origin-only loopback URL, a matching per-run
token, an initialized test database, and `REQUIRE_BROWSER_TESTS=true`:

```bash
dotnet publish Glosify/Glosify.csproj -c Release -o /tmp/glosify-browser-publish

REQUIRE_BROWSER_TESTS=true \
GLOSIFY_BROWSER_BASE_URL=http://localhost:5099 \
GLOSIFY_BROWSER_RUN_TOKEN=replace-with-a-unique-local-token \
ConnectionStrings__DefaultConnection='Server=127.0.0.1,1433;Database=glosify-browser;User Id=sa;Password=...;Encrypt=True;TrustServerCertificate=True' \
OPENAI_SECRET_KEY=browser-testing-placeholder \
Legal__ControllerName='Browser Test Operator' \
Legal__ContactEmail=browser-tests@example.invalid \
bash scripts/run-browser-tests.sh /tmp/glosify-browser-publish
```

Set `GLOSIFY_BROWSER_TRACE_DIRECTORY` to retain a Playwright trace for each
application journey. The launcher validates the private browser-test handshake
before any journey creates or changes data and always stops the published app.

Credential-gated direct OpenAI smoke tests can be enabled with
`RUN_OPENAI_SMOKE_TESTS=true`; they read `OPENAI_SECRET_KEY` from the environment
or the GlobeGlotter user-secret store.

The workflow in `.github/workflows/master_glosify.yml` validates pull requests.
A push to `master` applies the reviewed EF migration bundle and deploys Azure
Web App `glosify-app`. See [the deployment runbook](docs/DEPLOYMENT.md).

## Pull-request review

**Greptile is the only external automated reviewer.** Follow
[the review workflow](docs/REVIEW-WORKFLOW.md) for local verification, completed
review evidence, finding dispositions, and unavailable reviews.

Before merging, inspect Greptile's completed feedback for the latest PR revision,
fix confirmed issues, and explain rejected findings with evidence. Handle
duplicate reports once. If Greptile is unavailable, record the gap and perform
local code review without substituting another external service. CI and human
judgment still govern merge readiness. See [the agent guidance](AGENTS.md).

Greptile's GitHub app must be installed and this repository enabled in its
dashboard. Repository settings live in [.greptile/config.json](.greptile/config.json),
with review guidance and context in the same directory. Reviews are configured
to run again on PR updates, focus on logic and syntax, and publish a status check.
Automatic approval is disabled. This configuration does not add a required
branch-protection check.

A manual Greptile review can be requested with `@greptileai` on the PR when
needed. Verify that the completed review covers the latest PR head; unpushed
local changes are not covered. No additional GitHub Actions job or API-key
secret is needed for the GitHub app integration.

References: [Greptile setup](https://www.greptile.com/docs/quickstart),
[configuration reference](https://www.greptile.com/docs/code-review/greptile-config-reference),
and [manual reviews](https://www.greptile.com/docs/code-review/developer-essentials).

See [abuse controls and ElevenLabs rollout](docs/ABUSE-PROTECTION.md).
