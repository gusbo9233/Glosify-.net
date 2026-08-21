# Glosify

[![Build, test, and deploy](https://github.com/gusbo9233/Glosify-.net/actions/workflows/master_glosify.yml/badge.svg)](https://github.com/gusbo9233/Glosify-.net/actions/workflows/master_glosify.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**[Live app](https://glosify.se)** · [Case study](docs/portfolio-case-study.md) · [Architecture](docs/ARCHITECTURE.md) · [ADRs](docs/adr/) · [Tests](Glosify.Tests/)

Glosify is a language-learning app made with ASP.NET Core 10 MVC. It started as
a school project and became a portfolio project with a real database,
authentication, tests, Azure services, AI features, and a live deployment.

## Features

- Vocabulary and sentence quizzes with flashcards, typing, JSON import, and
  custom interactive quizzes.
- Anki-style collections with the FSRS-6 scheduler.
- An AI assistant that can create and edit quizzes and use books or saved
  transcripts as context.
- PDF reading, page translation, and file storage in Azure Blob Storage.
- Speaking practice with pronunciation assessment, coaching, and animated
  language-specific scenes.
- A Manifest V3 Chrome extension for live translated subtitles from tab audio.
  It uses the same account and AI credits as the web app. Audio is not stored.
- Classrooms with members, shared content, planning, assignments, results,
  SignalR chat, and Azure Communication Services calls.
- Public quiz and collection sharing, 69 learning languages, and a localized UI.
- ASP.NET Core Identity, optional Google and Microsoft login, bearer-token APIs,
  AI credit accounting, and optional Stripe credit packs.

| Speaking practice | Assistant quiz creation |
| --- | --- |
| [![Speaking practice](docs/screenshots/speaking-practice.png)](docs/screenshots/speaking-practice.png) | [![Assistant quiz creation](docs/screenshots/create-quiz-chat.png)](docs/screenshots/create-quiz-chat.png) |
| **Book reader** | **Live subtitles** |
| [![Book reader](docs/screenshots/book-quiz-assistant.png)](docs/screenshots/book-quiz-assistant.png) | [![Live subtitles](docs/screenshots/live-subtitles-in-action.png)](docs/screenshots/live-subtitles-in-action.png) |

## Design and stack

The repository has one ASP.NET Core web project. MVC and API controllers handle
HTTP work, feature services hold application logic, and EF Core talks directly
to SQL Server. There is no generic repository or separate domain assembly.

Main technologies are .NET 10, EF Core 10, SQL Server/Azure SQL, ASP.NET Core
Identity and SignalR, Microsoft Foundry, Azure AI Speech, Azure Translator,
Azure Communication Services, Azure Blob Storage, ElevenLabs Scribe v2, Stripe,
PdfPig, OpenTelemetry, xUnit, AngleSharp, and Playwright. Gemini can be selected
by configuration as a manual rollback. It is not automatic failover.

The live deployment uses one Azure App Service instance. See
[ADR 0001](docs/adr/0001-single-instance-state.md) for its limits and the work
needed before scale-out.

```text
Glosify/                         Web app and EF migrations
Glosify.Tests/                   .NET unit, integration, and contract tests
Glosify.BrowserTests/            Chromium user journeys
Glosify.ClientTests/             Browser JavaScript tests
Glosify.LiveSubtitles.Extension/ Chrome extension and tests
.foundry/                        Agent exports, evaluations, and datasets
docs/                            Guides, ADRs, and screenshots
scripts/                         Development and operations helpers
```

## Local setup

You need the .NET 10 SDK, Docker or another container runtime, and network access
for the first package and container downloads. Development uses only the local
SQL Server container, never Azure SQL.

On Apple Silicon, start Colima with Rosetta because the SQL Server image is amd64:

```bash
colima start --arch aarch64 --vm-type vz --vz-rosetta --cpu 4 --memory 4 --disk 40
```

Then run:

```bash
dotnet tool restore
docker compose -f docker-compose.dev.yml up -d

dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Server=localhost,1433;Database=glosifydb;User Id=sa;Password=Local_Dev_Only_1;Encrypt=True;TrustServerCertificate=True;" \
  --project Glosify

./scripts/dev-db-reset.sh
dotnet run --project Glosify
```

Register at `https://localhost:7032/Account/Register`. The login route is
`/login`. Email confirmation is off by default. External login buttons appear
only when their client IDs and secrets are configured.

Normal quiz, Anki, classroom, sharing, and UI work does not need Azure. Foundry,
Speech, Translator, Communication Services, Blob Storage, live subtitles, and
other external features need network access and valid configuration.
For local Azure authentication, run `az login`.

## Configuration and database

The app requires `ConnectionStrings:DefaultConnection`. Checked-in
[`appsettings.json`](Glosify/appsettings.json) has non-secret defaults. Put secrets
in .NET user secrets, environment variables, or Azure App Service settings.
Never commit credentials. Stripe setup is in [docs/STRIPE.md](docs/STRIPE.md).

The migration history starts with a complete `InitialCreate`. Startup never
changes the schema. To update or check a database, use:

```bash
dotnet ef database update --project Glosify
dotnet ef migrations has-pending-model-changes --project Glosify
```

`scripts/dev-db-reset.sh` drops only the fixed local development database and
then applies the migrations.

Foundry agent versions are pinned under `GenerativeAi:Foundry:Agents` in
[`appsettings.json`](Glosify/appsettings.json). Agent changes must be published as
a new immutable version in Foundry, exported to `.foundry/agents/`, and then
pinned in the same code change.

## Tests

Run the main test suites with:

```bash
dotnet test Glosify.Tests/Glosify.Tests.csproj
npm test --prefix Glosify.ClientTests
npm test --prefix Glosify.LiveSubtitles.Extension
```

CI also runs extension browser tests and nine Glosify Chromium journeys against
an empty migrated SQL Server database. For a local browser run:

```bash
pwsh Glosify.BrowserTests/bin/Debug/net10.0/playwright.ps1 install chromium
GLOSIFY_BROWSER_BASE_URL=http://localhost:5099 \
  dotnet test Glosify.BrowserTests/Glosify.BrowserTests.csproj
```

On macOS without PowerShell, set `GLOSIFY_BROWSER_EXECUTABLE_PATH` to a local
Chrome or Chromium executable.

Live Foundry smoke tests are optional:

```bash
RUN_FOUNDRY_SMOKE_TESTS=true \
dotnet test Glosify.slnx -c Release --filter Category=LiveFoundry
```

## Deployment

The workflow in `.github/workflows/master_glosify.yml` builds and tests pull
requests. A push to `master` also applies the reviewed EF migration bundle and
deploys to Azure Web App `glosify-app`. Production uses managed identity,
`/healthz` for liveness, and `/readyz` for SQL readiness.

Use the [deployment runbook](docs/DEPLOYMENT.md) for release, verification,
recovery, and Chrome Web Store steps.
