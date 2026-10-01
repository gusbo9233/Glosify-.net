# Assistant runtime

Every assistant message runs as a durable **run**: the request is accepted at once,
a background worker advances it one checkpointed step at a time, and the browser
watches it over server-sent events. Losing the browser request never cancels the
work. The request-reply endpoints (the web panel's older routes and the mobile
API) start the same kind of run and wait for it, so there is one execution path.

The design borrows from agent harnesses such as opencode: the conversation is a
list of typed parts, tools are self-describing, changes pass a permission gate,
context is managed by pruning then summarizing, and the prompt is laid out so the
provider can cache it.

## Conversation model

Messages (`assistant_messages`) are split into parts (`assistant_parts`):

| Part | Meaning |
| --- | --- |
| `text` | Visible text from the user or the assistant. |
| `quiz_link` | A link to a quiz created by this run, appended on completion and retained in chat history. |
| `tool` | One tool call: its input, state (`pending`, `completed`, `error`, `awaiting_approval`, `approved`, `awaiting_input`, `rejected`, `superseded`), the output the model receives, and the title the user sees. |
| `context` | App facts attached to the user message they came with: quiz, languages, the page being read, the selected book or transcript. |
| `reminder` | A runtime note to the model, such as unread source lines or an undone reply. |
| `summary` | A compaction summary standing in for older conversation. |
| `step` | The raw provider output items of one model call. |

The provider history is rebuilt from parts for every model call. Raw provider
items, including encrypted reasoning, replay only for the current run's last few
steps; everything else is rebuilt from text and tool parts. Chats saved before
parts existed replay their visible text only.

## Loop

A run's state (`assistant_runs.state_json`) names the next phase:

- **Model**: build the history, call the model once, and checkpoint its reply —
  text, and a `pending` tool part per call.
- **Tools**: run pending calls. Consecutive reads run in parallel, each in its own
  DI scope; writes run one per step inside a serializable transaction that also
  records the result.
- **Finish**: remind the model once about unread source lines or unfinished
  checklist items, place new quizzes (below), then complete.

The model ends a request by replying without tool calls; there is no `finish`
tool. A call repeated with identical arguments twice in a row is answered without
running. Six unproductive calls in a row pause the run; rereading unchanged results
counts as unproductive even when different read tools or pages alternate.

Each execution window allows `AssistantRuntime:MaxModelCalls` model calls (24)
and `AssistantRuntime:WindowSeconds` (300). A window renews automatically when
the run has saved new changes in that window and is not currently stalling.
Without that progress, the last call keeps the tools declared but sets
`tool_choice: none` and asks for a summary of what is done and what remains;
the run then pauses. Resume grants a new window. Transient
provider failures retry from the last checkpoint with backoff that honours
`Retry-After`; credit, budget, and quota failures pause with their reason.

Leases (two minutes, renewed every 20 seconds) and a revision concurrency token
fence every checkpoint, so a lost lease, Stop, or steering message discards a step
instead of racing it. Stop keeps what was saved. A message sent while a run works
is steering: calls not yet made are superseded and the reply continues in a new
message after it. Only one run per user is active; starting a request while a run
waits for the user in another chat stops that run.

## Tools

The selected learning language fixes the language of generated words and sentences.
For new quizzes, the model infers the translation language from the user's own
instructions, excluding pasted study material, and defaults to English when unclear.
Quiz creation rejects a learning language that differs from app context. Existing
quizzes retain their translation language when edited. Practice direction is chosen
when playing, so creation does not ask for it or generate reversed duplicates.

Each tool has a typed argument record. `ToolSchemas` derives an OpenAI strict-mode
schema from it with `JsonSchemaExporter` and parses calls against the same
serializer options, so invalid or unknown arguments come back to the model as a
correctable error for that call only. Tools never save anything: write tools
return the changes they propose, and the runtime applies them.

Each mode has a fixed tool list. Language: `quiz_overview`, `list_items`,
`search_items`, `add_items`, `edit_items`, `delete_items`, `create_quiz`,
`list_library`, `create_collection`, `rename_collection`, `move_quiz`,
`move_collection`, the book, transcript, and saved-translation readers,
`read_source`, `update_plan`, and `ask_user`. Freestyle uses prompt-and-answer
variants and no transcript tools. Content tools act on the chat's quiz or an
explicit `quiz_id`, always within the user's own quizzes.

`create_quiz` creates the quiz with at most 100 starter words and 100 sentences
and returns its id; further batches go through `add_items`. `read_source` pages a
long request (over 6,000 characters) by line number; the model sees the first 40
lines inline. It also retrieves original user messages by `message_id` from a
saved source catalog, including shorter messages covered by a conversation summary.
Source lookup is restricted to the user's current chat. `update_plan` keeps a checklist the user sees as progress.
`ask_user` offers clickable options and waits for the answer in the same run.

## Permissions and Undo

Adds, edits, new quizzes, new collections, and renames are saved immediately and
journaled in `assistant_changes` with their before and after state. Deletions and
moves wait for approval: the user can approve, always allow that kind in the chat
(`assistant_threads.approval_rules`), or decline with an optional reason that the
model receives. A quiz bound for a publicly visible collection is built privately
at the library root and moved in only when the run ends, after approval.

Undo reverts a finished run's journal newest first. A change is reverted only
while the item still matches what the run saved; anything the user changed since
is kept and reported. A created quiz is removed only once empty of its own
additions. The conversation gets a note so the model does not describe undone
changes as present.

Edits and deletions compare the quiz with a fingerprint taken at the last model
call; if the user changed it in between, the call is refused as a conflict and
the model re-reads before trying again.

## Prompt layout and context

The system prompt (`Services/Ai/Assistant/Prompts/*.md`, versioned by
`AssistantPrompts.Version`) and the tool list are identical for every request in a
mode. Per-message facts live in the stored context part; per-step state (the
checklist, unread lines, recent errors, the step-limit instruction) is sent as a
trailing developer message and never stored. Earlier requests are therefore a
strict prefix of later ones, which is what OpenAI's prompt cache reuses. A caller
that needs a narrower tool set passes `AllowedToolNames`, sent as `allowed_tools`
rather than by removing definitions. Cached input tokens are recorded on each
model invocation (`cached_prompt_tokens`) and summed on the run.

Credits are charged per token, so history is bounded by
`AssistantRuntime:ContextTokenBudget` (32,000 estimated tokens). Above half the
budget, settled tool output older than the newest
`ProtectedToolOutputTokens` (8,000) is cleared and replaced with a note. Above
that rolling tail, the latest distinct read results in the active run are retained
within half the context budget, so source text and quiz pages can be compared
together. The newest batch is always shown before it can be cleared. Above
four-fifths, the conversation before the current request is summarized with
`Prompts/compaction.md` into a `summary` part; later requests start from it.

## API

Cookie routes live at `/Assistant/Runs` (antiforgery applies to mutations);
bearer routes at `/api/assistant/runs`. Failures use the shared Problem Details
contract; ownership is checked on every route.

| Method and suffix | Behavior |
| --- | --- |
| POST `/chats/{threadId}` | `{ idempotencyKey, request: { message, contextQuizId, ... } }`; 202 with the run view and a status Location. Reusing a key with the same request returns the same run. |
| GET `/chats/{threadId}` | The chat's latest run, or null. |
| GET `/{id}` | The run view: status, reason, revision, parts, plan, question, approval, saved count, created quizzes, Undo availability. |
| GET `/{id}/events` | Server-sent events named `run`, one per revision, with the revision as the event id. The stream closes after 45 seconds or when the run ends; clients reconnect with `Last-Event-ID`. |
| POST `/{id}/steer` · `/cancel` | `{ revision, message? }`; apply to the latest revision. |
| POST `/{id}/approve` · `/reject` · `/answer` · `/resume` | `{ revision, always?, message?, answers? }`; the revision must match what the user saw. |
| POST `/{id}/undo` | Reverts a finished run; returns undone and kept counts with the run view. |

The request-reply endpoints keep their routes and response shapes. Their runs
never stop to ask: changes that need approval come back as a proposal for the
existing Apply and Reject endpoints, a question becomes the reply, and anything
that would pause ends the run with its reason.

## Advisory tool-use review

With `Jev:Enabled`, each settled tool call gets an `assistant_tool_evaluations`
row holding a decision snapshot (request, steering, context, offered tools,
arguments, recent results, outcome). The worker reviews them with the TypeSafe Jev
judge while idle, after the run has ended, and records findings against the call.
Reviews never block, change, or undo work; missing configuration, pricing,
credits, or an outage leave them unavailable. Configure `TYPESAFE_API_KEY`,
the pinned `Jev:Model`, and its token prices under
`AiUsage:MonthlyBudget:Models` before enabling.

## Deployment

Apply the `AssistantRunsAndParts` migration before deploying. It only adds tables
and nullable columns. Keep the already-deployed `AddDurableAssistantTasks` and
`AddAssistantTaskAttempts` migrations unchanged: their legacy tables and data remain
in place for compatibility and recovery, but the new worker does not resume old
tasks. Existing chat messages remain available; unfinished legacy tasks must be
submitted again as new runs. The production workflow stops the old application,
applies the migration bundle, and verifies the new artifact before reopening
traffic. The worker runs in the web app, so the existing
single-instance deployment ADR applies. There is no feature flag and no second
runtime: rolling back means redeploying the previous build. Runs and their parts,
changes, and evaluations cascade away with their chat, and count toward storage
quotas.

A lost provider response can cost a second paid request when a worker restarts
mid-step; the runtime does not promise exactly-once provider billing. Saved
changes are committed exactly once, with their tool result.

## Verification

```sh
dotnet test Glosify.Tests --filter 'FullyQualifiedName~Assistant|FullyQualifiedName~ChangeApplier|FullyQualifiedName~OpenAiGenerativeAi'
node --test Glosify.ClientTests/assistant-runs.test.js
```

`AssistantRunTests` drives the worker step by step over SQLite with a scripted
model: restart safety, stale leases, steering, Stop, retries, the step limit and
Resume, approvals, questions, conflicts with user edits, long sources, checklist
reminders, pruning and summaries, quiz placement, Undo, sync-mode behavior,
analytics, and the prompt-prefix invariant. `AssistantToolsTests` checks strict
schemas and tool behavior; `AssistantRunApiTests` covers the HTTP contract and the
event stream. No live-model run has been recorded for this runtime; the scripted
tests do not establish model quality.
