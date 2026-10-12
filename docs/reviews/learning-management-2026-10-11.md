# Learning-management implementation review — 2026-10-11

Follow-up: the six valid findings retained below are addressed in [valid-findings-2026-10-11.md](valid-findings-2026-10-11.md). The dispositions below describe the earlier implementation review.

## Scope and limitations

Assistant quiz/library/Anki management, direction changes, shared flashcards, and live screen refresh. The working tree already contained a substantial assistant-runtime rewrite and unrelated work; those changes were preserved. Reviews include runtime dependencies to interpret this feature, not approval of all existing work.

DeepSource cannot assess the unpushed local revision. No PR, push, deployment, or production-data mutation was requested or performed. The SQL-backed tests and full application browser suite require a local database environment unavailable in this session. Isolated Playwright browser checks use synthetic content and actual changed JavaScript/CSS, so they do not validate authentication/database integration.

## Verification

- Final solution: 1,166 .NET tests passed; 33 database-dependent tests skipped.
- Browser test project: 39 skipped because the database-backed host was unavailable.
- Client suite: 130 passed.
- Isolated real browser: live fragment replacement without navigation; chat draft preserved; dirty-form deferral and explicit update; regular two/Anki four ratings; right swipe Got it/Easy; left swipe Again; hidden-card swipe reveals only; numeric Anki grading.
- `git diff --check` passed.

## Qodo assessment

Initial operation `14b8408d-f82d-4efc-aa47-b0297344ba59` completed with auto depth, full analysis, complete coverage of its submitted paths. Reviewers: issue, compliance, skills; skipped: spec, UI, persona, cross-repo. The scoped snapshot omitted some existing runtime wiring, producing findings inconsistent with the actual working tree. These are assessed below rather than treated as instructions.

- **The application cannot build with new tools** — `ae13296a-9d87-41a4-8844-4130a991de61` [Correctness/action_required]. Dismissed against the working tree: the legacy registry is removed, AddAssistantTools is registered, and the solution builds. The earlier scoped snapshot omitted those preexisting changes.

- **The application cannot build with runs** — `0979fb02-0a11-465c-a19a-73fc1a9c48fa` [Correctness/action_required]. Dismissed against the working tree: GlosifyContext contains the durable DbSets and Glosify/Migrations/20260930151848_AssistantRunsAndParts.cs supplies the existing migration. No new schema is required by the learning-management changes.

- **Assistant messages fail with missing routes** — `0d63d705-a826-455b-964e-ad5309977649` [Correctness/action_required]. Dismissed: Controllers/AssistantRunsController.cs implements Assistant/Runs, including chat submission, discovery and events.

- **Queued assistant runs never execute** — `978fe2ba-55af-4708-8961-be85fb9d4291` [Reliability/action_required]. Dismissed: ApplicationServiceExtensions.cs registers AssistantRunStore and AddHostedService<AssistantRunWorker>().

- **A concurrent command can be lost** — `6034f4a3-e11d-4a0a-a026-997071850d2a` [Correctness/action_required]. Dismissed: AssistantRunConfiguration.cs configures Revision with IsConcurrencyToken().

- **Interrupted evaluations never resume** — `5c39c73a-cef2-4345-b1a9-c1c912a434ba` [Reliability/remediation_recommended]. Deferred, preexisting: advisory evaluation rows can remain evaluating after worker interruption. This affects post-run evaluation availability, not execution of the requested learning changes. Recommend a separate lease/recovery change and restart regression test.

- **Undo can duplicate a learner's content** — `b9962f80-825a-4d9a-b60b-4ae97287dc08` [Correctness/remediation_recommended]. Fixed: edit Undo now checks for another word/sentence with the original text before restoring. A regression test for both item types verifies later learner content is retained without duplication.

- **Focused chats can alter other sentences** — `4ff62e79-e7a7-4dff-839f-61a9ea6bf23e` [Correctness/remediation_recommended]. Fixed: focused-word edit/delete calls reject sentence changes. Regression tests cover both tools.

- **Proposed-change counts omit proposals** — `9dd0fd91-70a9-49b6-b462-5da14cbcb6a5` [Observability/remediation_recommended]. Deferred, preexisting: turn-level ProposedChangeCount uses SavedChanges and therefore undercounts refused/cancelled proposals. Per-tool proposal telemetry is separate. Recommend a dedicated telemetry correction with mixed approval/cancellation coverage.

- **New assistant runs never execute** — `634b0a6f-a113-4058-a407-f12c9ce730f1` [Correctness/action_required]. Dismissed as the same wiring concern as 978fe2ba-55af-4708-8961-be85fb9d4291; production worker registration exists.

Final follow-up operation `24ec5d68-02fc-4863-bcfa-698d126927d9` includes the fixes and additional existing wiring context. Completed at auto depth with full analysis and complete coverage of submitted paths. Reviewers ran issue/compliance/skills; spec/UI/persona/cross-repo were skipped. The submitted path scope is not a review of the entire dirty working tree.

## Follow-up findings

- **Assistant runs fail before generating replies** — `54824506-0e8e-438b-a347-2a1c4c78b4d2` [Correctness/action_required]. Dismissed against the working tree: Glosify.csproj embeds Services/Ai/Assistant/Prompts/*.md with the exact logical resource name used by the loader. Runtime tests exercise this path successfully. Project file now included in follow-up context.

- **Sentence flashcards cannot be revealed** — `d7076def-b766-41b3-abcd-b2f9a9364d12` [Correctness/action_required]. Fixed: parse the session sentence ID as a GUID before comparison; regression tests use actual WordService loader IDs and verify both valid reveal and rejection/reset after source edits.

- **Old run updates can stop a new run** — `3e594908-cb8d-4685-bd4c-9bc43350293a` [Reliability/remediation_recommended]. Fixed: superseded runs cannot replace the active run; historical Undo still broadcasts content changes. Stream identity and discovery guards ignore delayed events/responses. Client tests cover stale events, Undo, and delayed discovery.

- **Direction changes lose removed Anki notes** — `f9719f11-df1f-4d26-b1d0-ead111c6316f` [Correctness/remediation_recommended]. Fixed: direct inclusion is retained regardless of temporary source inactivity, then transferred to the selected direction. Regression verifies deleted source stays inactive and regains selected-direction membership after restoration.

- **Large libraries hide later quizzes** — `87d31141-0c16-473a-b7f3-84ad11ae1b6d` [Correctness/remediation_recommended]. Fixed: list_library supports offset with deterministic ordering, next_offset, and accurate truncation metadata. Regression retrieves all 306 quizzes without duplicates.

- **Translation pages repeat the last item** — `710b9631-7fec-41a7-95c9-ac0110f18b8f` [Correctness/informational]. Deferred, preexisting outside quiz/Anki scope: saved translation paging clamps an out-of-range offset to the final item. Recommend returning an empty page with a boundary regression test in a separate translation-tools change.


Final fixes review operation: `106d634d-5db5-461f-861c-4ed7e9e7c968` (completed: auto depth, full analysis, complete submitted-path coverage; issue/compliance/skills ran, spec/UI/persona/cross-repo skipped). Full solution rerun after fixes: 1,166 passed, 33 skipped; browser project 39 skipped; client suite 130 passed.

## Final review assessment

- **Long histories slow Anki changes** — `340fa0e1-400a-4fd1-b489-ffa50bb333e0` [Performance/remediation_recommended]. Confirmed performance limitation, retained and recorded: conservative aggregate snapshots include review history so approval and Undo detect intervening study activity. Reads and journal size grow with lifetime review count even for settings/name changes. Recommend operation-specific snapshots with compact review concurrency evidence, verified against SQL Server and review/Undo races before replacing this guard. Large-history performance was not benchmarked; this is not a clean performance sign-off.

- **Study summaries slow as reviews accumulate** — `ae9495ed-7c65-4e01-9c9c-1a8321f61cee` [Performance/remediation_recommended]. Confirmed performance limitation, retained and recorded: statistics materialize review ratings/timestamps then aggregate in memory. Recommend database-side aggregates with provider-compatible date filtering and a long-history benchmark. The current implementation returns correct tested metadata but its cost grows with review history.

- **Paged listings can skip saved items** — `84ce9a14-13c8-4480-9660-4bf989e833ae` [Correctness/remediation_recommended]. Deferred, preexisting outside quiz/Anki scope: book and transcript offset pagination lacks a unique timestamp tie-breaker. Recommend adding ID tie-breakers and equal-timestamp boundary tests in a separate change.

No additional production edits followed this review. These outstanding recommendations and the unavailable SQL/browser/DeepSource coverage are explicitly retained; the review is not claimed as a clean bill of health for the entire working tree.
