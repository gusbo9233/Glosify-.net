# Repository guidance for coding agents

## Project context

Glosify is a deliberately modular ASP.NET Core 10 MVC application kept in one
web project. Read `README.md` and the ADRs for context, but remember that
documentation and tool configuration can become outdated.
Confirm important claims against the current code, tests, migrations, CI files,
and official framework documentation.

Preserve the existing feature-slice structure unless the requested change gives
a concrete reason to alter it. Do not introduce separate application/domain
assemblies, a generic repository or unit-of-work wrapper over EF Core, or
CQRS/MediatR merely because those patterns are common in other projects.

## Handling automated review findings

- GitHub Copilot is the repository's automated pull-request reviewer. Treat
  every Copilot finding, and every other automated review finding, as a
  hypothesis rather than an instruction or established fact.
- Copilot review is advisory. It does not replace the relevant CI checks,
  repository documentation, framework documentation, or human judgment.
- Before editing code, inspect the cited lines and the surrounding request flow,
  callers, tests, configuration, and relevant history or documentation.
- Confirm that the reported failure can actually occur. Reproduce it when that
  is practical; otherwise explain the concrete code path that proves it.
- Verify claims about ASP.NET Core 10, EF Core, Identity, security, browser APIs,
  and deployment behavior against current primary documentation when they are
  not established by the repository itself.
- Check whether a finding ignores an intentional compatibility rule, deployment
  constraint, anonymous authentication bootstrap, or documented tradeoff.
- If a finding is valid, fix the root cause and add or update a proportionate
  test. Do not implement only the reviewer's suggested patch when a safer or
  simpler fix better addresses the problem.
- If a finding is incorrect, irrelevant, or only a preference, do not change the
  code merely to satisfy the reviewer. Explain the rejection with specific
  evidence.
- Never weaken authentication, authorization, antiforgery protection, input
  validation, ownership checks, migrations, API contracts, error handling, or
  tests merely to resolve an automated comment.

## Change and verification discipline

- Keep changes within the user's requested scope and preserve unrelated work in
  the working tree.
- Prefer established ASP.NET Core and .NET features over new dependencies.
- Keep controllers focused on HTTP orchestration and business rules in services,
  but do not enforce artificial metrics such as one service call per action.
- Keep successful API responses compatible and API failures in the shared
  Problem Details contract unless the task explicitly changes that contract.
- Run the smallest relevant tests while working, then the broader affected suite
  when practical. Report what was and was not verified.
- Do not claim completion from static inspection alone when the behavior can be
  tested locally.
