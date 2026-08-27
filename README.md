# Glosify

[![Build, test, and deploy](https://github.com/gusbo9233/Glosify-.net/actions/workflows/master_glosify.yml/badge.svg)](https://github.com/gusbo9233/Glosify-.net/actions/workflows/master_glosify.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**[Live app](https://glosify.se)** · [Case study](docs/portfolio-case-study.md) · [Architecture](docs/ARCHITECTURE.md) · [ADRs](docs/adr/) · [Tests](Glosify.Tests/)

Glosify is an ASP.NET Core 10 MVC language-learning application with quizzes,
FSRS-6 study collections, books, saved assistant chats, and a Chrome extension
for live translated subtitles.

## AI services

All generative, text-agent, and vision work goes directly from the Glosify server
to the OpenAI Responses API. The model is fixed in code to `gpt-5.6-luna`; there
is no model picker, configured alternative, or provider fallback. Prompts, JSON
schemas, and function tools are defined and executed in this repository. Glosify
replays its own saved history and every Responses request uses `store: false`.

Azure Speech provides server-side text-to-speech for book reading. The Enhanced
subtitle relay connects server-side to `gpt-realtime-translate` while
the Scribe alternative uses ElevenLabs Scribe v2 followed by Azure Translator.
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
OpenAI, Azure Speech, Azure Translator, Azure Blob Storage, ElevenLabs Scribe v2,
Stripe, OpenTelemetry,
xUnit, AngleSharp, and Playwright.

```text
Glosify/                         Web app and EF migrations
Glosify.Tests/                   .NET unit, integration, and contract tests
Glosify.BrowserTests/            Chromium user journeys
Glosify.ClientTests/             Browser JavaScript tests
Glosify.LiveSubtitles.Extension/ Chrome extension and tests
docs/                            Guides, ADRs, and screenshots
scripts/                         Development and operations helpers
```

## Local setup

You need the .NET 10 SDK and Docker or another container runtime. Development
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

Register at `https://localhost:7032/Account/Register`; the login route is
`/login`. Ordinary quiz, Anki, sharing, and UI development does not
need external service credentials. Azure-backed features use `az login` during
local development.

### Test direct OpenAI locally

The HTTPS launch profile enables Enhanced subtitles and allowlists the pinned
development extension callback. It still reads the exact `OPENAI_SECRET_KEY`
from the Glosify user-secret store.

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

Chrome must trust the local ASP.NET Core HTTPS certificate. Scribe mode also
requires its retained ElevenLabs and Azure Translator configuration.

The application never changes the schema at startup. Use:

```bash
dotnet ef database update --project Glosify
dotnet ef migrations has-pending-model-changes --project Glosify
```

## Tests

```bash
dotnet test Glosify.slnx -c Release
npm test --prefix Glosify.ClientTests
npm test --prefix Glosify.LiveSubtitles.Extension
```

Credential-gated direct OpenAI smoke tests can be enabled with
`RUN_OPENAI_SMOKE_TESTS=true`; they read `OPENAI_SECRET_KEY` from the environment
or the Glosify user-secret store.

The workflow in `.github/workflows/master_glosify.yml` validates pull requests.
A push to `master` applies the reviewed EF migration bundle and deploys Azure
Web App `glosify-app`. See [the deployment runbook](docs/DEPLOYMENT.md).
