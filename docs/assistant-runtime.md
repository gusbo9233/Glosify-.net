# Durable assistant execution and advisory tool-use review

The durable harness runs inside the web application and checkpoints to SQL Server.
A browser request starts work; losing that request does not cancel the work. This
replaces the former long-running HTTP dependency when enabled. It does not establish
which browser/proxy caused the previously observed approximately 60-second disconnects.

## Enable in stages

Apply `AddDurableAssistantTasks` and `AddAssistantTaskAttempts` with the repository's
normal migration deployment workflow before enabling the worker. Both are additive.
Do not use `EnsureCreated` against an existing production database. The deployment
workflow runs the migration bundle to its latest migration while workers are stopped;
it must not target the older fractional-credit migration, which would omit (or later
downgrade) the assistant tables.

Defaults are off in `appsettings.json`:

- `AssistantRuntime:Enabled`: durable worker and synchronous compatibility adapters.
- `AssistantRuntime:WebEnabled`: web chat uses task APIs, progress, Stop, Resume,
  steering, and concrete approvals. Requires `Enabled`.
- `AssistantRuntime:CoverageEnabled`: requests above 4,000 characters are internally
  partitioned into immutable sections of at most 2,400 UTF-16 characters. The accepted
  request maximum is 50,000 characters. Enable this with the web rollout.
- `Jev:Enabled`: advisory tool-use evaluations after requested work completes.

Each execution window defaults to 300 seconds, 24 provider attempts, and 64,000
processed tokens. Preflight uses conservative character-based token estimates;
confirmed provider totals replace estimates when available. Retries consume the
same allowance. Resume explicitly grants another window. Content already saved
is retained when work pauses or is stopped. Existing AI credits, monthly cost
limits, ownership checks, and resource accounting also apply.

Start with durable execution in staging; exercise interruption/restart tests; then
enable the web and coverage flags. Enable Jev separately after evaluating the
supported languages. Roll back by disabling admission/UI flags and stopping the
worker with `Enabled=false`; retain tables and checkpoints for later recovery.
The application's existing single-instance deployment ADR still applies to other
features; assistant leases alone do not make the whole application scale-out safe.

## API and state

Cookie-authenticated routes live at `/Assistant/Tasks`; bearer-authenticated routes
live at `/api/assistant/tasks`. Cookie mutations retain the global antiforgery filter.
Every operation checks the authenticated owner. Failures use shared Problem Details.

| Method and suffix | Behavior |
| --- | --- |
| POST `/chats/{threadId}` | `{ idempotencyKey, request: { message, contextQuizId, ... } }`; returns 202 and a status Location |
| GET `/chats/{threadId}` | Latest task for reopening a chat, or null |
| GET `/{id}` | Status, revision, counts, artifact links, concrete approval changes, and tool-use findings |
| POST `/{id}/steer` | `{ revision, message }`; supersedes unexecuted calls at the next step boundary |
| POST `/{id}/cancel` | `{ revision }`; stops future work and retains saved content |
| POST `/{id}/resume` | `{ revision }`; grants another execution window to paused/waiting work |
| POST `/{id}/approve` | `{ revision }`; approves only the concrete proposal at that revision |

Reusing a submission key with the same request returns the existing task; different
content conflicts. Approve and Resume carry revision preconditions: they apply only to
the proposal or pause the user saw. Stop and steering apply to the latest revision,
because a running task advances its revision at every checkpoint; the server retries
them when the worker commits first. Identical successful commands can be retried.
One nonterminal task per user implies one per chat. A task
can be queued, running, retry_wait, awaiting_input, awaiting_approval, paused,
completed, cancelled, or failed. Paused work must be resumed or cancelled before
starting another task for that user.

Two worker loops claim database leases. Leases last two minutes, renew every 20
seconds in a separate scope, and fence all content commits. Three consecutive
renewal failures stop the step before the lease can expire under it, so another
worker cannot repeat its provider request. A checkpoint that loses a race with Stop
or steering releases the lease at once. Model attempts are
recorded before network calls; raw provider outputs, including encrypted reasoning
items, are checkpointed before tool dispatch. Decision snapshots are persisted
before execution. Content changes, outcomes, and progress commit together.
Checkpoints use read-committed transactions and the task row's lock. Only the
content save is serializable: it rechecks the quiz and writes in one short
transaction, so a user's edit cannot land between the two. Data tools run before
that transaction because they only read and build proposals. Checkpointed history
is compacted with the request: earlier chat text goes first, then whole older
exchanges; this task's request and the latest exchange remain.
Transient retries are scheduled in SQL and release the worker; hidden SDK retries
are disabled only for durable requests. Lost provider responses can require another
paid request: this system does not promise exactly-once provider billing.

Tool handlers retain their ownership and argument checks. The runtime supplies strict
schemas, validates tool arguments, reports partial/invalid results to the generator,
rechecks quiz content before applying edits, caches bounded quiz reads, executes up
to three independent reads in separate scopes, and performs writes serially. Model
batches do not have to execute as one transaction. When the recheck finds that the
user changed the quiz, the call returns a `conflict` and the rest of its batch is
superseded, so the generator replans from current content. A conflict is not an
argument error: the same call may be repeated, and dropping it to keep the user's
edit does not block `finish_task`. Completed
calls are never repeated merely because a later call failed. Destructive operations,
bulk edits, and library moves require review. New quizzes remain private while being
built, including when a collection was supplied.

Source coverage records UTF-16 spans referencing successful saved calls or explicit
exclusions. It checks that source text was accounted for; it cannot independently
prove that every generated translation or vocabulary extraction is semantically
correct. `finish_task` checks the coverage ledger, unfinished drafts, approval state,
and unresolved mutation errors. Rejected/skipped mutation targets stay in a
checkpointed ledger until matching targets are successfully saved or proposed;
unrelated writes and approval of a partial batch do not clear them. Steering can
supersede these obligations when the user changes the request. An item without a
stable identity is resolved explicitly with `resolve_rejected_item`, referencing a
later successful correction of the same mutation type; evidence cannot be reused.
Typed word/sentence deduplication outcomes are intentional exclusions, not errors. Ordinary non-mutating questions can finish with one
model response. Three unproductive steps request a changed approach; six pause.

Legacy synchronous endpoints wait for a durable task when enabled and retain their
proposal/Apply behavior. HTTP cancellation stops their wait only. Their clients
cannot resume, steer, or stop a task, so a synchronous task never waits: `ask_user`
ends the turn with the question (the answer starts a new task that replays it),
and anything that would pause a web task ends it as `failed` with its partial
proposals. Either way the user's active slot is released. A new task replays the
chat's earlier text messages from both sides, never legacy tool-call or tool-result
rows without their partners. The new web task
surface saves authorized changes batch by batch. Thread/account deletion cascades
operational tasks, attempts, and evaluations, preventing orphaned workers from
committing. These records are operational content and count toward storage quotas.

## Jev is a tool-use judge

Configure `TYPESAFE_API_KEY` through server secrets and pin `Jev:Model` (default
`jev-1.13.0`). No key is sent to the browser. Calls use only the official endpoint
`https://api.typesafe.ai/v1/systemone`.

Configure the existing AI monthly budget to meter provider `typesafe` and add the
pinned model's token prices in SEK under `AiUsage:MonthlyBudget:Models` before
turning on evaluations. The shared pricing validator currently requires both token
prices positive: use a conservative positive output price; this integration records
zero billed output tokens. Do not substitute an OpenAI model price or disable the
budget to enable Jev. Missing pricing leaves reviews unavailable. User credit
reservations and settlements use the existing ledger, independently of generation.

The immutable snapshot contains the active request, steering, selected tool and
arguments, offered tool descriptions/policies, context identifiers, and recent
results. The evaluator adds the actual outcome and asks separate typed questions
about operation choice, resource targeting, word/sentence destination, requested
scope, duplicate mutations, prerequisites, and handling earlier failures.

Translation fields are removed from structured evaluator input. Text is considered
only when needed for tool routing, such as `add_word` versus `add_sentence`.
Multiword vocabulary and Freestyle items remain legitimate. Grammar, translations,
style, and teaching quality are explicitly outside the rubric. Source text and
arguments remain untrusted data. Jev never authorizes, blocks, repairs, or rewrites
an operation, and its findings are not fed into the generator's correction loop.

Findings use fixed codes and UI templates; no explanatory prose is generated by Jev.
A default confidence threshold of 0.8 controls whether a finding is displayed as
uncertain, not whether an action executes. This threshold is provisional and must
be calibrated with labeled language-specific traces. TypeSafe documents stronger
English performance than other languages. Format correctness is not judgment
correctness.

Checks run after completion and only while generation workers are otherwise idle.
They consume the remaining execution-window budget. Missing configuration, credit
limits, outages, malformed responses, or expired budgets leave reviews unavailable
or pending; they never undo or fail completed work. The UI shows evaluated/total
counts separately from task completion. Budget-deferred checks do not poll forever.

## Verification and monitoring

Deterministic tests cover persistence/restart, mutations committed once, stale lease
fencing, steering, cancellation, bounded retries, budget resume, concrete deletion
approval, concurrent edits, rollback after a content save, 150-word/150-sentence
batches, 50,000-character partitioning, repeated supplied lyric excerpts with a
later ending, deletion cascades, and advisory parsing. Further tests cover Stop and
steering against an advanced revision and a checkpoint race, immediate lease release
after a lost race, heartbeat renewal failures, synchronous tasks that ask or would
pause, data tools running outside the content transaction, a user edit during tool
execution, checkpointed compaction, replayed chat history, and saved counts when the
applier drops an item. Coverage rejects early
completion and survives budget pauses and worker restart. Evaluation-worker tests
verify that findings, outages, and exhausted budgets preserve completed content and
use the captured decision even after the user edits the quiz.
HTTP tests cover 202/Location, replay, ownership, Problem Details, size validation,
and antiforgery. Browser-module tests cover reconnect and idempotent submission.
With `RUN_SQLSERVER_TESTS=true`, disposable SQL Server databases also run competing
claims and, with the production retrying execution strategy, the restart, approval,
concurrent-edit, batch, checkpoint-race, tool-scope, saved-count, and evaluation
scenarios. SQLite does not model SQL Server's range locks.

```sh
dotnet test Glosify.Tests --filter 'FullyQualifiedName~AssistantRuntimeTests|FullyQualifiedName~AssistantTaskApiTests|FullyQualifiedName~JevToolUseEvaluatorTests'
node --test Glosify.ClientTests/assistant-tasks.test.js
```

For the opt-in real-provider evaluation, set `RUN_JEV_EVALS=true` and
`TYPESAFE_API_KEY`, then run `JevLiveEvaluationTests`. It reports classification
accuracy, uncertainty, elapsed time, and input tokens on synthetic Polish tool-use
traces. Those live results are not established by mocked tests.
The first live smoke run on 2026-09-27 with `jev-1.13.0` matched 7 of 8 labeled
checks and returned uncertainty on the remaining check (no confidently incorrect
verdicts). It used 11,915 input tokens across eight requests and took 2.565 seconds
in aggregate. This small synthetic sample is not a supported-language accuracy
benchmark and does not enable the rollout flags. The local server credential is
stored in .NET user secrets; it is not included in the repository.
No live model run of the full supplied song or browser-driven long-disconnect test
has been performed; deterministic worker tests advance the clock beyond 60 seconds
and restore a fresh worker scope without a browser request.

The `Glosify.Assistant.Runtime` meter exports model-attempt, saved-change, retry,
task-transition, and evaluation counters. Durable attempts preserve detailed
request/result/error evidence regardless of optional analytics capture. Use the
existing credit ledger for cost accounting and never log provider credentials.
