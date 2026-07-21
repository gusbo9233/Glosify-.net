# Glosify

Glosify is an ASP.NET Core MVC language-learning app. It combines vocabulary
and sentence quizzes with flashcards, typing practice, avatar conversations,
shared collections, PDF text extraction, and AI-assisted content creation.

## Features

- Account-based quiz and collection management with ASP.NET Core Identity.
- Vocabulary and sentence practice with flashcard and typing modes.
- AI-assisted vocabulary generation, image text extraction, and quiz repair.
- Assistant conversations scoped to individual quizzes.
- Azure-powered speaking practice with animated language-specific avatars,
  typed chat, pronunciation assessment, coaching, and validated scene actions.
- PDF uploads backed by Azure Blob Storage and extracted page text.
- AI credit accounting for trials, usage, and admin grants.
- Bearer-token mobile API endpoints under `/api/*`.

## Tech stack

- .NET 10 and ASP.NET Core MVC
- Entity Framework Core 10 with SQL Server or Azure SQL
- ASP.NET Core Identity
- Microsoft Foundry and Azure AI Speech
- Azure Blob Storage and Azure Monitor OpenTelemetry
- Gemini as an explicit deployment-level rollback
- PdfPig for PDF text extraction
- xUnit, ASP.NET Core MVC testing, and AngleSharp

## Repository layout

```text
Glosify/                  Web application
Glosify.Tests/            Automated tests
Glosify/Migrations/       EF Core migrations
Glosify/wwwroot/          Static assets
.foundry/                 Speaking-agent evaluations, datasets, and rubrics
docs/                     Product, architecture, and operations documentation
.github/workflows/        Azure deployment workflow
```

## Configuration

The application requires a SQL Server connection named `DefaultConnection`.
Checked-in `appsettings.json` contains non-secret defaults for Foundry, Speaking,
Blob Storage, AI credits, and logging. Keep credentials in environment
variables, .NET user secrets, or Azure App Service settings—never in Git.

Local Azure access uses `DefaultAzureCredential`; run `az login` before using
Foundry, Speech, or Blob Storage features. Production uses managed identity and
does not require Foundry or Speech keys.

See the detailed configuration references for
[generative AI](docs/foundry-generative-ai.md#configuration) and
[Speaking](docs/azure-speaking-practice.md#application-configuration).

## Local development

1. Install the .NET 10 SDK.
2. Configure `ConnectionStrings__DefaultConnection` and any required credentials
   with environment variables or user secrets.
3. Sign in to Azure for Azure-backed features:

   ```bash
   az login
   ```

4. Restore and build:

   ```bash
   dotnet restore
   dotnet build
   ```

5. Apply database migrations:

   ```bash
   dotnet ef database update --project Glosify
   ```

6. Run the app:

   ```bash
   dotnet run --project Glosify
   ```

Most routes require authentication. The login route is `/login`.

## Tests

Run the test suite with:

```bash
dotnet test
```

The suite covers navigation, authorisation, sharing, quizzes, assistant flows,
AI credits, Speaking sessions and APIs, Foundry usage, and Speech tokens.

Live Foundry smoke tests are opt-in:

```bash
RUN_FOUNDRY_SMOKE_TESTS=true \
dotnet test Glosify.slnx -c Release \
  --filter Category=LiveFoundry
```

They use `DefaultAzureCredential`. See the
[Foundry validation guide](docs/foundry-generative-ai.md#validation-and-live-smoke-tests)
for optional overrides.

## Documentation

Start with the [documentation index](docs/README.md).

- [Azure-powered speaking practice](docs/azure-speaking-practice.md)
- [Foundry generative AI](docs/foundry-generative-ai.md)
- [Database diagram](docs/database-diagram.md)
- [Rewarded ads for AI credits](docs/rewarded-ads-for-credits.md)

## Database

The EF Core context is `Glosify.Data.GlosifyContext`. The database includes
Identity and application tables for quizzes, words, sentences, collections,
assistant messages, AI credits, and book documents.

See the [database diagram](docs/database-diagram.md) for the complete model.

## Deployment

The workflow in `.github/workflows/master_glosify.yml` builds and deploys the
app to Azure Web App `glosify` when `master` is pushed.

Practice sessions, opaque Speaking session mappings, and mobile sign-in codes
are stored in process. The app therefore assumes a single instance, and active
sessions are lost when it restarts.

Production settings, managed-identity roles, telemetry, and the temporary Gemini
rollback procedure are documented in the [Foundry guide](docs/foundry-generative-ai.md)
and [Speaking guide](docs/azure-speaking-practice.md).

## Public repository hygiene

The repository ignores local settings, `.env` files, generated documents,
`.DS_Store`, and tool-specific configuration. The versioned `.foundry` files
contain evaluation metadata, prompts, datasets, and rubrics, but no credentials.
Review generated evaluation content before committing it, and rotate any secret
that has ever been pushed.
