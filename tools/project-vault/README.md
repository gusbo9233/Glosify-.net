# Project Vault

A local, Obsidian-inspired workspace for **agent-authored visual documentation**.
User questions and agent understanding drive its content. Static analysis is a
reference and a sanity check; it does not generate or organize the document library.

## Run

```sh
bash tools/project-vault/scripts/setup.sh
bash tools/project-vault/scripts/start.sh /absolute/path/to/repository
```

Open http://127.0.0.1:5188. The tool uses React/TypeScript and ASP.NET Core 10 and
runs independently of the application being documented. No static index is required.
The server initializes the repository's local tool location. For a different
repository, pass its path to `start.sh`; use `PROJECT_VAULT_HOME` if MCP runs before
the server has initialized that repository.

## Read and ask

The library contains deliberately authored documents, not one page per code symbol.
Each repository starts with an empty authored library. Users and agents add only the
workflows and explanations that are useful for that project.

- Open a document, explore its diagram, or switch to Diagram + explanation / Read.
- Click a state or transition to see its meaning, conditions, effects and evidence.
- Follow linked documents and backlinks without losing canvas position.
- Move cards, bookmark documents, and attach Markdown annotations. These remain
  separate from agent-authored revisions. Removed targets are marked unresolved.
- Use **Ask about this** or **Request documentation** to save a question. Copy the
  handoff into your current Codex task. The agent reads the request through MCP,
  investigates code, publishes an answer, and updates the request's status.
- Request statuses are open, in-progress, partial and answered. Partial answers
  retain their outstanding questions. A documentation request does not authorize
  changing application behavior.

The source inventory remains available through **Source reference inventory**. Its
existing notes, proposals and layouts are preserved. It is not the primary view.

## Agent authoring through MCP

The bundled plugin is in `plugins/project-vault`. It runs a local stdio MCP server:

```sh
dotnet tools/project-vault/server/bin/Debug/net10.0/ProjectVault.dll mcp \
  --repo /absolute/path/to/repository --tool-root /absolute/path/to/tools/project-vault
```

Primary tools:

| Tools | Purpose |
| --- | --- |
| `vault_documents`, `vault_document` | Find/read published documents, drafts and revision history |
| `vault_source` | Read source ranges with current evidence hashes, without indexing |
| `vault_save_document` | Save a draft or publish an authored revision |
| `vault_document_requests`, `vault_save_request` | Read requests and record progress/results |
| `vault_document_notes`, `vault_save_document_note` | Read/write separate user annotations |
| `vault_document_impacts`, `vault_document_status` | Identify dependency/context changes needing review |
| `vault_review_document` | Record an evidenced review when the explanation remains accurate |

Read the explicit MCP input schemas. `vault_save_document` takes `document`,
`expectedVersion`, and `publish`. New documents use expectedVersion 0. Draft saves,
publication and reviews advance the envelope version. Publication verifies evidence,
links, transition endpoints, source context and the expected version, retaining the
previous published revision on failure. Conceptual states do not have to correspond
to methods or persisted enum values. Publish linked detail pages before their parent.

Evidence records contain an independent ID, repository-relative path, line/endLine,
and current hash from `vault_source`. Diagram items reference those evidence IDs.
Documents list supporting dependencies to help find review impacts. Do not treat
matching hashes as proof that an explanation is correct or complete.

## Maintenance

After each coherent implementation step and relevant verification, the agent checks
`vault_document_impacts`, considers broader semantic effects, and revises affected
documents or records a specific unchanged-review rationale with reconciled evidence.
It checks `vault_document_status` before continuing or finishing.

```sh
dotnet tools/project-vault/server/bin/Debug/net10.0/ProjectVault.dll document-check \
  --repo . --tool-root tools/project-vault
```

A dependency edit, deletion, branch change or worktree change marks the relevant
document **Needs review**. Unrelated file edits do not invalidate all documents.
The last published explanation remains readable. Retry failed publication/review
once, then explicitly report remaining blockage.

`vault_refresh` and the `refresh` CLI command rebuild optional static reference data.
They **cannot mark authored documents reviewed**. The existing `status`/`check` CLI
commands report that legacy index; use `document-status`/`document-check` for authored
content. The updated Stop hook uses the document check, not the index check.

Install/update the personal plugin with the plugin-creator workflow, then start a
new Codex task and approve its hooks through the normal trust flow. Hooks detect
outstanding dependency/context review, not every semantic change or implementation
step. The skill provides the per-step procedure. No embedded agent runtime or
background execution service is added to the web app.

## Storage and compatibility

Inside `.project-visualization/`:

- `documents/<id>.json`: authored draft, published revision, evidence and review history;
  one atomic envelope per document, independent of extracted symbol IDs.
- `requests/<id>.json`: versioned documentation questions, targets and results.
- `document-notes/<id>.json`: separate user annotations containing Markdown.
- `document-layouts/<id>.json`: shared card positions; `library.json` holds bookmarks.
- `local/`: ignored tool location and update locks. Viewports and tabs stay in browser storage.
- Existing `current.json`, `snapshots/`, `notes/`, `interpretations/`, `proposals/`
  and `layout.json` retain their legacy meaning and are never converted to authored pages.

The server remains loopback-only, with host/origin checks for browser writes,
repository-contained source access, and cross-process locking/version checks.
The HTTP document endpoints use the same service as MCP and return Problem Details
on invalid input or conflicts. Browser publication updates are polled every five seconds.

## Verification and limits

```sh
npm test --prefix tools/project-vault
npm run build --prefix tools/project-vault
dotnet build tools/project-vault/server --no-restore
```

Document integration tests run in two independent .NET repository fixtures without
an index. They exercise MCP schemas, drafts, publishing, conflicts, invalid evidence,
requests, reviews, branch changes, preserved annotations/layouts and unresolved items.
Legacy extraction/integration tests remain included.

An authored document is a source-reviewed explanation, not a runtime trace. The tool
validates supplied references but cannot prove complete business understanding,
discover every indirect dependency, or guarantee runtime outcomes. Azure observations
remain repository evidence. Runtime tracing, cloud hosting, collaboration, live Azure
inventory and Obsidian export remain extensions.

## Four connected levels

The default **Workflows** map leads through authored workflow details, action details,
and function details. A shared model page can be opened from contracts and data-flow
links. The Library still lists all authored pages. Breadcrumbs record the path followed;
canvas viewport, selection, diagram and display mode are kept per document locally.
Double-click a linked card or select it and use its Open detail button. Missing action
or function detail can be requested from the selected item.

Documents have a backward-compatible `kind`: `workflow-overview`, `workflow`, `action`,
`function`, `model`, or the default `explanation`. Existing revisions are not rewritten.
Document, node and transition `detailLinks` contain `targetId`, `relation` and `label`.
Relations `workflow`, `expands`, `calls`, `uses-model` enforce the target page kind;
`related` links general reading. Models and functions may be reused by several paths.

Function pages add a structured `contract`; model pages add `fields`. Both require a
`primarySource` binding. `vault_declarations` lists exact C# declaration IDs within one
file; `vault_declaration` returns the full selected declaration with path, identity,
file hash, line range and reviewed code. This lookup parses only the requested file
and does not build or index the repository. Overload identities include containing
namespace/type and parameter syntax; aliases or renamed declarations require explicit
reconciliation rather than guessing a substitute. Partial/duplicate identities in a
single file are reported ambiguous.

`vault_document_source` and `GET /api/documents/{id}/source?version=N` compare a revision's
reviewed excerpt with the currently resolved declaration. The UI displays reviewed
source by default, with a separate current-source view when changed. Missing or
ambiguous source never selects a different overload. Publication verifies the complete
binding; changed declaration text requires a new publication. An unchanged review may
reconcile moved lines or other file changes when the declaration text is identical.
The source pane links to the containing C# file through a local plain-text endpoint.

Repositories can build paths such as Workflows → workflow → action → function →
shared model. No example workflows or generated inventories are bundled as authored
content.
