# Glosify

Glosify is an ASP.NET Core MVC application for building and practicing language-learning quizzes. It supports vocabulary lists, sentence practice, flashcards, typing sessions, public/shared collections, PDF book text extraction, and AI-assisted quiz generation and repair.

## Features

- Account-based quiz and collection management with ASP.NET Core Identity.
- Vocabulary and sentence practice with flashcard and typing quiz modes.
- AI-assisted vocabulary generation, image text extraction, and quiz repair through Gemini-compatible configuration.
- In-app assistant threads scoped to quizzes.
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
- PdfPig for PDF text extraction
- xUnit, ASP.NET Core MVC testing, AngleSharp

## Repository Layout

```text
Glosify/                 Web application
Glosify.Tests/           Automated tests
Glosify/Migrations/      EF Core migrations
Glosify/wwwroot/         Static assets
docs/database-diagram.md Mermaid database diagram
.github/workflows/      Azure deployment workflow
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
```

`Glosify/appsettings.json` contains non-secret defaults such as model names, blob container name, AI credit settings, and logging levels.

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

The tests use in-memory EF Core databases where practical and cover navigation, authorization, public sharing, assistant flows, AI credits, and quiz practice services.

## Database

The EF Core context is `Glosify.Data.GlosifyContext`. The database includes Identity tables plus application tables for quizzes, words, sentences, collections, assistant messages, AI credits, and book documents/pages.

See [docs/database-diagram.md](docs/database-diagram.md) for the Mermaid ER diagram.

## Deployment

The workflow in `.github/workflows/master_glosify.yml` builds and deploys the app to Azure Web App `glosify` when `master` is pushed.

Note: practice sessions (flashcards/typing) and mobile sign-in codes are stored in in-process memory, so the app assumes a single instance; scaling out or restarting drops active sessions.

Production configuration should be supplied through Azure app settings, including:

- `ConnectionStrings__DefaultConnection`
- `GEMINI_API_KEY`
- `Acs__ConnectionString`
- Blob storage settings
- OAuth client credentials, if external login is enabled
- `Admin__Emails__0` and additional indexed admin emails as needed

## Public Repo Hygiene

This repo intentionally ignores local development settings, `.env` files, generated document exports, `.DS_Store`, and tool-specific local config. Keep secrets out of commits and rotate any credential that has ever been pushed.
