# Glosify documentation

## Product and operations

- [Azure-powered speaking practice](azure-speaking-practice.md) — `/Speaking`
  user experience, APIs, live Azure resources, managed identity, prompt agents,
  Speech integration, privacy, rate limits, telemetry, evaluation suites,
  costs, testing, and troubleshooting.
- [Foundry generative AI](foundry-generative-ai.md) — assistant function
  calling, typed vocabulary repair, image text extraction, managed identity,
  telemetry, rollback, and live smoke validation.
- [Rewarded ads for AI credits](rewarded-ads-for-credits.md) — rewarded-ad
  integration and credit flow.
- [Database diagram](database-diagram.md) — Mermaid entity-relationship
  diagram for the Glosify database.

## Guides and source documents

- [Glosify: A Guide to the Codebase, .NET, and Azure](glosify-guide.pdf) — a
  code-first tour of the application, modern .NET, C#, and the Azure services
  and operational concepts that support the deployed system; see also its
  [LaTeX source](glosify-guide.tex).
- [ASP.NET Core MVC tutorial source](aspnet-mvc-tutorial.tex).

## Foundry evaluation artifacts

The versioned production evaluation workspace is stored at
[`.foundry`](../.foundry/). Its metadata, suite definitions, datasets, and
custom evaluator rubrics are described in the
[speaking-practice operations guide](azure-speaking-practice.md#foundry-evaluation-suites).

Review generated datasets and evaluator rubrics before a paid batch run. Store
result artifacts under `.foundry/results/` so the evaluated agent version and
outcome remain auditable.
