# Glosify project overview and case study

[Open the live application](https://glosify.se) · [Return to the repository README](../README.md)

Glosify is the main full-stack project I use to learn how a complete .NET web
application fits together. It started as a school project and grew into a
deployed language-learning application with authentication, SQL persistence,
AI features, browser tests, and an Azure delivery pipeline.

## What the application does

Glosify brings several language-learning activities into one account:

- save vocabulary and sentences in quizzes and collections;
- practise with flashcards, typing exercises, and custom quizzes;
- upload PDF books, translate pages, and create learning material from them;
- practise conversations in animated speaking scenes;
- manage classrooms, assignments, chat, and calls;
- create and update quizzes through an assistant;
- translate audio from a Chrome tab with the Live Subtitles extension.

## Product tour

### Speaking practice

[![Speaking practice with an AI bartender scene](screenshots/speaking-practice.png)](screenshots/speaking-practice.png)

The speaking feature combines an animated scene, text or microphone input,
generated replies, optional translations, and structured scene actions. The
application owns and validates every action before changing the scene.

### Assistant-driven quiz creation

[![The assistant creating a Polish vocabulary quiz](screenshots/create-quiz-chat.png)](screenshots/create-quiz-chat.png)

The assistant can suggest changes to a user's learning library. The application
shows the changes for review, and the user can apply or reject them. The model
does not write directly to the database.

[![Generated vocabulary and assistant context](screenshots/generated-quiz.png)](screenshots/generated-quiz.png)

### Practice modes

[![A typing quiz](screenshots/quiz-practice.png)](screenshots/quiz-practice.png)

Vocabulary can be practised in both directions. Each practice session tracks
progress, while the saved quiz data remains in SQL Server.

### Books and context

[![A book page with the assistant preparing a quiz](screenshots/book-quiz-assistant.png)](screenshots/book-quiz-assistant.png)

PDF files are stored in Azure Blob Storage. Extracted page text is saved in the
database and can be translated, read aloud, or supplied to the assistant as
explicit context.

### Live translated subtitles

[![Translated subtitles over a Chrome video](screenshots/live-subtitles-in-action.png)](screenshots/live-subtitles-in-action.png)

[![Live Subtitles extension settings](screenshots/live-subtitles-settings.png)](screenshots/live-subtitles-settings.png)

The Manifest V3 extension is integrated with the Glosify web application. The
user connects it through the website using a one-time, PKCE-protected code. The
extension exchanges that code for Identity bearer credentials and then uses the
same account, APIs, and AI credit balance as the website.

The extension captures audio from the active tab and opens a short-lived,
authenticated WebSocket relay through Glosify. The translated text is displayed
over the current page. Audio is relayed in memory and is not stored. Saving the
original-language transcript is a separate user choice; saved transcripts can
be managed in the website and selected as context for the assistant.

## Architecture

```mermaid
flowchart LR
    Browser["Browser"]
    Extension["Chrome extension"]

    subgraph WebApp["ASP.NET Core 10 application"]
        MVC["MVC controllers and Razor views"] --> Services["Feature services"]
        API["Bearer-authenticated JSON APIs"] --> Services
        Relay --> Services
        Services --> EF["Entity Framework Core"]
    end

    Browser --> MVC
    Extension --> API
    Extension --> Relay["Short-lived WebSocket relay"]
    EF --> SQL["SQL Server / Azure SQL"]
    Services --> OpenAI["OpenAI Responses and realtime translation"]
    Services --> Speech["Azure AI Speech"]
    Services --> Storage["Azure Blob Storage"]
    Services --> ACS["Azure Communication Services"]
```

Controllers deal with HTTP concerns such as routes, request validation, and
response types. Feature services contain the application logic. EF Core is used
directly instead of hiding it behind a generic repository layer.

The application remains one web project. I have not split it into extra projects
because that would not solve a current problem. Internal interfaces are used
when they make responsibilities clearer or make code easier to test.

## What I have worked with

### ASP.NET Core

- MVC controllers and Razor views for the server-rendered website.
- Controller-based JSON APIs for mobile and extension clients.
- ASP.NET Core Identity with cookie and bearer authentication.
- A global antiforgery policy for unsafe MVC requests.
- Problem Details with stable error codes for API failures.
- Request validation, ownership checks, and named rate limits.
- Dependency injection and typed configuration options.
- Development OpenAPI output at `/openapi/v1.json`.

### Data and external services

- One clean `InitialCreate` migration for the current schema.
- SQL Server locally and Azure SQL in production.
- A reviewed EF migration bundle runs before application deployment.
- Blob compensation removes uploaded files when later persistence fails.
- Managed identity is used for supported Azure resources.
- Optional AI, speech, and storage providers stay outside readiness checks.

### Testing and delivery

The current automated suite contains:

- a broad .NET unit, integration, and contract suite, with direct OpenAI smoke
  tests credential-gated and skipped by default;
- 29 dependency-free JavaScript tests;
- four Playwright journeys covering accounts, quizzes, custom quizzes, and
  assistant chat management;
- a CI migration check that applies the schema to an empty SQL Server database
  and checks for pending model changes.

GitHub Actions publishes the application, builds the EF migration bundle, signs
in to Azure with OpenID Connect, applies migrations, and deploys to App Service.
CodeQL, secret scanning, push protection, Dependabot, and protected-branch
checks are enabled for the public repository.

## Current limitations

The portfolio deployment intentionally runs as one App Service instance. Some
session and coordination state remains in memory. That keeps this learning
project understandable and inexpensive, but it means active in-memory sessions
do not survive a restart and the app should not be scaled to multiple instances
without more work.

[ADR 0001](adr/0001-single-instance-state.md) records the current state and a
future Redis, SignalR backplane, and data-protection upgrade path.

## What I would improve next

- Replace older screenshots as the interface changes.
- Add accessibility checks to the browser suite.
- Split the largest remaining JavaScript files after behavior is covered.
- Add distributed state only if the deployment needs multiple instances.
- Continue moving expected application failures to typed domain outcomes.

I have kept the project as one application instead of adding layers only to make
it look more complex. My goal is to show what I have learned, explain the choices
I made, test important behavior, and be honest about what still needs work.
