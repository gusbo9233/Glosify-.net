# Glosify documentation

## Product and operations

- [Azure-powered speaking practice](azure-speaking-practice.md) — `/Speaking`
  user experience, APIs, live Azure resources, managed identity, prompt agents,
  Speech integration, privacy, rate limits, telemetry, evaluation suites,
  costs, testing, and troubleshooting.
- [Rewarded ads for AI credits](rewarded-ads-for-credits.md) — rewarded-ad
  integration and credit flow.
- [Database diagram](database-diagram.md) — Mermaid entity-relationship
  diagram for the Glosify database.

## Guides and source documents

- [Glosify guide](glosify-guide.pdf) and its
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
