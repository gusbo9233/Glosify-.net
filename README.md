# Glosify

Glosify is an ASP.NET Core MVC application for building and practising
language-learning quizzes. It supports vocabulary lists, sentence practice,
flashcards, typing sessions, language-bound avatar conversations, public/shared
collections, PDF book text extraction, and AI-assisted quiz generation and
repair.

## Features

- Account-based quiz and collection management with ASP.NET Core Identity.
- Vocabulary and sentence practice with flashcard and typing quiz modes.
- AI-assisted vocabulary generation, image text extraction, and quiz repair through Gemini-compatible configuration.
- In-app assistant threads scoped to quizzes.
- Azure-powered speaking practice with three animated avatars per supported language, typed chat, pronunciation assessment, and coaching.
- PDF book uploads with Azure Blob Storage and extracted page text.
- AI credit accounting for trial grants, reservations, usage debits, and admin grants.
- Bearer-token API endpoints for mobile clients under `/api/*`.
- Azure Web App deployment workflow for pushes to `master`.

## Tech Stack

- .NET 10 / ASP.NET Core MVC
- Entity Framework Core 10
- SQL Server / Azure SQL
- ASP.NET Core Identity
- Azure Blob Storage
- Gemini via `Mscc.GenerativeAI`
- Azure AI Foundry, Azure AI Speech, and Azure Monitor OpenTelemetry
- PdfPig for PDF text extraction
- xUnit, ASP.NET Core MVC testing, AngleSharp

## Repository Layout

```text
Glosify/                  Web application
Glosify.Tests/            Automated tests
Glosify/Migrations/       EF Core migrations
Glosify/wwwroot/          Static assets, including Speaking CSS, JS, and Speech SDK
.foundry/                 Speaking-agent evaluation suites, datasets, and rubrics
docs/                     Product, architecture, and operations documentation
.github/workflows/        Azure deployment workflow
```

## Configuration

Do not commit local secrets. Use environment variables, .NET user secrets, or host-level app settings.

Required for local startup:

```bash
ConnectionStrings__DefaultConnection="Server=...;"
GEMINI_API_KEY="..."
```

Common optional settings:

```bash
GEMINI_STRUCTURED_MODEL="..."
GEMINI_ASSISTANT_MODEL="..."
GEMINI_VISION_MODEL="..."
GEMINI_TIMEOUT_SECONDS="180"
Acs__ConnectionString="endpoint=https://<your-resource>.communication.azure.com/;accesskey=<key>"
Authentication__Google__ClientId="..."
Authentication__Google__ClientSecret="..."
Authentication__Microsoft__ClientId="..."
Authentication__Microsoft__ClientSecret="..."
BlobStorage__AccountName="..."
BlobStorage__ContainerName="..."
Admin__Emails__0="admin@example.com"

Speaking__ProjectEndpoint="https://<foundry-resource>.services.ai.azure.com/api/projects/<project>"
Speaking__ModelDeployment="gpt-5.4-mini"
Speaking__Agents__Bartender__Name="glosify-bartender"
Speaking__Agents__Bartender__Version="1"
Speaking__Agents__Kasia__Name="glosify-kasia"
Speaking__Agents__Kasia__Version="1"
Speaking__Agents__Mietek__Name="glosify-mietek"
Speaking__Agents__Mietek__Version="1"
Speaking__Agents__Maarja__Name="glosify-maarja"
Speaking__Agents__Maarja__Version="1"
Speaking__Agents__Karl__Name="glosify-karl"
Speaking__Agents__Karl__Version="1"
Speaking__Agents__Liis__Name="glosify-liis"
Speaking__Agents__Liis__Version="1"
Speaking__Agents__Hanna__Name="glosify-hanna"
Speaking__Agents__Hanna__Version="1"
Speaking__Agents__Jonas__Name="glosify-jonas"
Speaking__Agents__Jonas__Version="1"
Speaking__Agents__FrauSchneider__Name="glosify-frau-schneider"
Speaking__Agents__FrauSchneider__Version="1"
Speaking__Agents__Oksana__Name="glosify-oksana"
Speaking__Agents__Oksana__Version="1"
Speaking__Agents__Andriy__Name="glosify-andriy"
Speaking__Agents__Andriy__Version="1"
Speaking__Agents__PanMykola__Name="glosify-pan-mykola"
Speaking__Agents__PanMykola__Version="1"
Speech__Endpoint="https://<speech-resource>.cognitiveservices.azure.com"
Speech__ResourceId="/subscriptions/<subscription>/resourceGroups/<group>/providers/Microsoft.CognitiveServices/accounts/<speech-resource>"
Speech__Region="<region>"
APPLICATIONINSIGHTS_CONNECTION_STRING="..."
```

`Glosify/appsettings.json` contains non-secret defaults such as model names, blob container name, AI credit settings, and logging levels.

Speaking practice uses `DefaultAzureCredential`; do not configure a Foundry or
Speech key in browser-accessible settings. See the
[Azure speaking-practice guide](docs/azure-speaking-practice.md#identity-and-access)
for roles and local authentication.

## Local Development

1. Install the .NET 10 SDK.
2. Configure local secrets with environment variables or user secrets.
3. Restore and build:

```bash
dotnet restore
dotnet build
```

4. Apply database migrations to your configured SQL Server database:

```bash
dotnet ef database update --project Glosify
```

5. Run the app:

```bash
dotnet run --project Glosify
```

The app requires authentication for most routes. The login route is `/login`.

## Tests

Run the test suite with:

```bash
dotnet test
```

The tests use in-memory EF Core databases where practical and cover navigation,
authorisation, public sharing, assistant flows, AI credits, quiz practice
services, Speaking API antiforgery and rate limits, user-bound speaking
sessions, structured Foundry usage accounting, and keyless Speech tokens.

## Documentation

Start with the [documentation index](docs/README.md).

- [Azure-powered speaking practice](docs/azure-speaking-practice.md) documents
  the complete `/Speaking` experience, API contract, live Azure deployment,
  identity, data lifecycle, costs, Foundry evaluations, and validation.
- [Database diagram](docs/database-diagram.md) documents the EF Core model.
- [Rewarded ads for AI credits](docs/rewarded-ads-for-credits.md) documents the
  rewarded-credit integration.

## Database

The EF Core context is `Glosify.Data.GlosifyContext`. The database includes Identity tables plus application tables for quizzes, words, sentences, collections, assistant messages, AI credits, and book documents/pages.

See [docs/database-diagram.md](docs/database-diagram.md) for the Mermaid ER diagram.

## Deployment

The workflow in `.github/workflows/master_glosify.yml` builds and deploys the app to Azure Web App `glosify` when `master` is pushed.

Note: practice sessions (flashcards, typing, and Speaking), opaque Speaking
session mappings, and mobile sign-in codes are stored in process, so the app
assumes a single instance. Scaling out or restarting drops active sessions.

Production configuration should be supplied through Azure app settings, including:

- `ConnectionStrings__DefaultConnection`
- `GEMINI_API_KEY`
- `Acs__ConnectionString`
- Blob storage settings
- OAuth client credentials, if external login is enabled
- `Admin__Emails__0` and additional indexed admin emails as needed
- `Speaking__ProjectEndpoint`, `Speaking__ModelDeployment`, and all twelve pinned
  `Speaking__Agents__*` names and versions
- `Speech__Endpoint`, `Speech__ResourceId`, and `Speech__Region`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`, when speaking telemetry is enabled

The App Service managed identity needs `Azure AI User` on the Foundry account
and `Cognitive Services Speech User` on the Speech resource. Production does not
require a Foundry or Speech key.

## Public Repo Hygiene

This repo intentionally ignores local development settings, `.env` files,
generated document exports, `.DS_Store`, and tool-specific local config. The
versioned `.foundry` files contain evaluation metadata, prompts, datasets, and
rubrics, but no resource keys or access tokens. Review generated evaluation
content before committing it, keep secrets out of commits, and rotate any
credential that has ever been pushed.
