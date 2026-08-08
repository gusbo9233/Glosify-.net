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
- xUnit, ASP.NET Core MVC testing, and AngleSharp

## Repository layout

```text
Glosify/                  Web application
Glosify.Tests/            Automated tests
Glosify/Migrations/       EF Core migrations
Glosify/wwwroot/          Static assets
scripts/                  Development helper scripts
.foundry/                 Speaking-agent evaluations, datasets, and rubrics
docs/                     Product, architecture, and operations documentation
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

See the detailed configuration references for
[generative AI](docs/foundry-generative-ai.md#configuration) and
[Speaking](docs/azure-speaking-practice.md#application-configuration).

## Local development

Development runs against a SQL Server container on your own machine, never against
the Azure database. Nothing in this section costs money or requires a network.

1. Install the .NET 10 SDK and a container runtime. On Apple Silicon the SQL Server
   image is amd64-only, so the VM needs Rosetta translation:

   ```bash
   colima start --arch aarch64 --vm-type vz --vz-rosetta --cpu 4 --memory 4 --disk 40
   ```

2. Start the database:

   ```bash
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
   `https://localhost:7032/Identity/Account/Register`:

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

### Why `dev-db-reset.sh` instead of `dotnet ef database update`

The migration history starts mid-life: no migration creates `Quizzes` and the other
original tables, because the production database predates the first migration. So
migrations cannot build a database from nothing, and `dotnet ef database update`
fails on the first `ALTER` against an empty one.

`scripts/dev-db-reset.sh` creates the schema from the current model with
`dotnet ef dbcontext script`, then records every existing migration as applied. The
result matches the model and accepts future migrations normally, so once it has run
`dotnet ef database update` behaves as usual. Re-run the script whenever you want a
clean database; `docker compose -f docker-compose.dev.yml down -v` throws the data
away entirely.

### Working against the Azure database

Deliberate, and rare. The connection string holds no secret — it authenticates with
your Azure identity — but anything you run reaches live data:

```bash
ConnectionStrings__DefaultConnection='Server=tcp:glosify.database.windows.net,1433;Initial Catalog=glosifydb;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Authentication="Active Directory Default";' \
  dotnet ef migrations script <previous> <latest> --project Glosify
```

Review the SQL and apply it deliberately. `Database:ApplyMigrationsOnStartup` stays
`false` so nothing migrates production by merely starting up.

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
- [Live Subtitles Chrome extension](docs/live-subtitles-extension.md)
- [Database diagram](docs/database-diagram.md)
- [Rewarded ads for AI credits](docs/rewarded-ads-for-credits.md)

## Database

The EF Core context is `Glosify.Data.GlosifyContext`. The database includes
Identity and application tables for quizzes, words, sentences, collections,
assistant messages, AI credits, and book documents.

See the [database diagram](docs/database-diagram.md) for the complete model.

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

Migrations are not applied by the workflow. `Database__ApplyMigrationsOnStartup`
is on in the `glosify-app` settings, so the app applies whatever is still pending
each time it starts, under its own managed identity, which holds `db_ddladmin` on
`glosifydb`. The default in `appsettings.json` stays `false`, so a local run never
migrates a database on its own.

That only covers the migrations compiled into the deployed assembly, so it cannot
run a migration before the deploy that carries it. Applying one ahead of its
deploy means running `dotnet ef database update` against the production database,
which needs two things: `ConnectionStrings__DefaultConnection` in the environment,
because `GlosifyContextFactory` otherwise points design-time commands at a local
LocalDB, and a SQL server firewall rule for the client address.

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
