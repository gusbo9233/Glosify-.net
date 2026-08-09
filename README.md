# Glosify

Glosify is an ASP.NET Core MVC language-learning app. It combines vocabulary
and sentence quizzes with flashcards, typing practice, avatar conversations,
shared collections, PDF text extraction, and AI-assisted content creation.

## Features

- Account-based quiz and collection management with ASP.NET Core Identity.
- Vocabulary and sentence practice with flashcard and typing modes.
- AI-assisted vocabulary generation, image text extraction, and quiz repair.
- Assistant conversations with selectable context: a quiz to act on, plus a book
  or saved transcript to read from.
- Azure-powered speaking practice with animated language-specific avatars,
  typed chat, pronunciation assessment, coaching, and validated scene actions.
- PDF uploads and deletion backed by Azure Blob Storage, with extracted page text.
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
- xUnit, ASP.NET Core MVC testing, AngleSharp, and Playwright

## Repository layout

```text
Glosify/                  Web application
Glosify.Tests/            Automated tests
Glosify.BrowserTests/     Chromium user-journey tests
Glosify.ClientTests/      Dependency-free JavaScript module tests
Glosify/Migrations/       EF Core migrations
Glosify/wwwroot/          Static assets
scripts/                  Development helper scripts
.foundry/                 Speaking-agent evaluations, datasets, and rubrics
docs/adr/                 Architecture decision records
.github/workflows/        Azure deployment workflow
```

## Configuration

The application requires a SQL Server connection named `DefaultConnection`.
Checked-in `appsettings.json` contains non-secret defaults for Foundry, Speaking,
Blob Storage, AI credits, and logging. Keep credentials in environment
variables, .NET user secrets, or Azure App Service settings—never in Git.

`appsettings.Development.json` is git-ignored and holds non-secret overrides only.
Secrets belong in user secrets, which live outside the repository folder:
`dotnet user-secrets list --project Glosify`.

Local Azure access uses `DefaultAzureCredential`; run `az login` before using
Foundry, Speech, or Blob Storage features. Production uses managed identity and
does not require Foundry or Speech keys.

## Local development

Development runs against a SQL Server container on your own machine, never against
the Azure database. Nothing in this section costs money or requires a network.

1. Install the .NET 10 SDK and a container runtime. On Apple Silicon the SQL Server
   image is amd64-only, so the VM needs Rosetta translation:

   ```bash
   colima start --arch aarch64 --vm-type vz --vz-rosetta --cpu 4 --memory 4 --disk 40
   ```

2. Restore the repository-pinned EF tool and start the database:

   ```bash
   dotnet tool restore
   docker compose -f docker-compose.dev.yml up -d
   ```

3. Point the app at it. The connection string lives in user secrets rather than in
   any file, so it stays outside the repository folder entirely:

   ```bash
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
     "Server=localhost,1433;Database=glosifydb;User Id=sa;Password=Local_Dev_Only_1;Encrypt=True;TrustServerCertificate=True;" \
     --project Glosify
   ```

4. Create the schema:

   ```bash
   ./scripts/dev-db-reset.sh
   ```

5. Run the app and register a local account at
   `https://localhost:7032/Account/Register`:

   ```bash
   dotnet run --project Glosify
   ```

Most routes require authentication. The login route is `/login`. Local sign-in needs
no network: email confirmation is off in development, and the Google and Microsoft
buttons only appear when their client IDs are configured, so an offline machine
falls back to email and password. An account whose email is listed under
`Admin:Emails` gets the admin routes.

Azure-backed features — Foundry, Speech, Blob Storage — still need `az login` and a
network. Everything else, including the whole assistant UI, works offline.

### Database lifecycle

The checked-in history starts with one complete `InitialCreate` migration. A new
database is supported directly—there is no generated-schema shortcut and no forged
migration history:

```bash
dotnet ef database update --project Glosify
dotnet ef migrations has-pending-model-changes --project Glosify
```

`scripts/dev-db-reset.sh` is only a convenience for dropping the disposable local
database, recreating it, and running that same migration command. Startup never
changes schema.

### Working against the Azure database

Deliberate, and rare. The connection string holds no secret — it authenticates with
your Azure identity — but anything you run reaches live data:

```bash
ConnectionStrings__DefaultConnection='Server=tcp:glosify.database.windows.net,1433;Initial Catalog=glosifydb;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Authentication="Active Directory Default";' \
  dotnet ef migrations script <previous> <latest> --project Glosify
```

Review the SQL and apply it deliberately. There is no startup-migration switch, so
starting the web process can never change schema.

## Tests

Run the deterministic .NET and JavaScript suites with:

```bash
dotnet test Glosify.Tests/Glosify.Tests.csproj
npm test --prefix Glosify.ClientTests
npm test --prefix Glosify.LiveSubtitles.Extension
```

The suite covers navigation, authorisation, sharing, quizzes, assistant flows,
AI credits, Speaking sessions and APIs, Foundry usage, and Speech tokens.

The CI browser job starts the app against an empty migrated SQL Server database and
runs four Chromium journeys. For a local run, install Chromium once and provide the
test host:

```bash
pwsh Glosify.BrowserTests/bin/Debug/net10.0/playwright.ps1 install chromium
GLOSIFY_BROWSER_BASE_URL=http://localhost:5099 \
  dotnet test Glosify.BrowserTests/Glosify.BrowserTests.csproj
```

On a Mac without PowerShell, set `GLOSIFY_BROWSER_EXECUTABLE_PATH` to an installed
Chromium/Chrome executable instead.

Live Foundry smoke tests are opt-in:

```bash
RUN_FOUNDRY_SMOKE_TESTS=true \
dotnet test Glosify.slnx -c Release \
  --filter Category=LiveFoundry
```

They use `DefaultAzureCredential`; provider-specific values can be supplied with
the same environment-variable names as the corresponding `appsettings.json`
sections.

## Database

The EF Core context is `Glosify.Data.GlosifyContext`. The database includes
Identity and application tables for quizzes, words, sentences, collections,
assistant messages, AI credits, and book documents.

## Deployment

The workflow in `.github/workflows/master_glosify.yml` builds and deploys the
app to Azure Web App `glosify-app` when `master` is pushed. That is the site
behind `glosify.se`, and it is now the only web app in the resource group: the
older `glosify` web app that sat beside it has been deleted, so a setting or a
log can no longer be read off the wrong site. The SQL server is also called
`glosify`, which is a different resource.

Use `https://glosify.se` when a client needs a base URL, not the App Service
hostname. External sign-in builds its Google `redirect_uri` from the request
host, and only the custom domain is registered with Google, so the App Service
hostname reaches the app but fails the OAuth request.

CI proves the migration history against an empty SQL Server database, checks for
pending model changes, and builds an EF migration bundle. Deployment executes that
reviewed bundle before publishing the web artifact. The deployment identity uses the
passwordless `PRODUCTION_SQL_CONNECTION_STRING` secret and owns schema changes.
The GitHub OIDC identity `identity-glosify` holds `db_ddladmin`; the web app's
`glosify-app` managed identity is runtime-only and holds only `db_datareader` and
`db_datawriter`.

The portfolio deployment intentionally remains single-instance. Its in-memory state,
failure semantics, and Redis/SignalR/data-protection upgrade path are recorded in
[ADR 0001](docs/adr/0001-single-instance-state.md).

## Public repository hygiene

The repository ignores local settings, `.env` files, generated documents,
`.DS_Store`, and tool-specific configuration. The versioned `.foundry` files
contain evaluation metadata, prompts, datasets, and rubrics, but no credentials.
Review generated evaluation content before committing it, and rotate any secret
that has ever been pushed.
