# Assistant checkpoint follow-up

The owner requested the three remaining fixes after rechecking the 15 findings
against the current assistant runtime. The old findings refer to `a843adb`;
the implementation base for this follow-up is the local `8002d27` checkpoint.

- **Accounting contention:** ordinary progress checkpoints now prepare tracked
  changes before their single accounted save. The revision concurrency token
  still rejects stale saves and the enclosing transaction rolls back their
  messages, parts, and accounting changes together. Content application, final
  quiz placement, and pruning retain the early fence because they write during the action. They
  still hold the shared accounting lock until commit; this is a reduction in
  unnecessary locking, not removal of the site's quota serialization. Accounting
  coverage, limits, and content transaction isolation are unchanged.
- **Long questions:** interactive `ask_user` keeps the complete question in the
  tool metadata and question view. The bounded run reason contains only
  `Waiting for your answer.` Sync replies still contain the full question.
- **Stale follow-ups:** after steering receives HTTP 409, a terminal refresh
  stops retries against the finished run. If that same run is still current,
  sending starts a new run with the full original input and the existing
  idempotency key behavior. Conflicts on an active run still retry steering;
  network errors alone never trigger a new run.

The other findings do not require further changes: timeout handling, destination
language validation, lease revisions, paused steering, fairness, legacy error
mapping, and prompt duplication were addressed in the current runtime. Immediate
edits with Undo, chat-owned context, the 50,000-character input contract, and
uncapped provider output are intentional current policies. The old `complete`
protocol and legacy runner no longer apply. The output policy is unchanged.

The checkpoint fence relies on EF's documented [optimistic concurrency
behavior](https://learn.microsoft.com/en-us/ef/core/saving/concurrency) and the
existing [explicit transaction](https://learn.microsoft.com/en-us/ef/core/saving/transactions).
The tests use real SQLite accounting and a zero-row concurrency update to
verify rollback, rather than assuming an exception alone proves atomicity.

Verification on an isolated export of `8002d27` plus this follow-up:

- Targeted assistant .NET tests: **84 passed, 0 failed, 7 skipped**.
- Final full solution: **1,166 passed, 0 failed, 32 skipped**; the application-browser
  project additionally has **39 skipped** tests.
- Full client suite: **144 passed, 0 failed**.
- New coverage includes accounting query order, exact content-byte totals,
  conflict rollback/recovery, 1,000/1,001/12,000-character questions and answers,
  all terminal follow-up statuses, lost start-response idempotency, active-run
  conflicts, and network failures.
- The new SQL Server Stop-versus-preparation race test is opt-in and skipped
  because a local SQL Server test database is not configured. No SQL Server
  contention or load result is claimed.

Qodo auto review `d4b2bf2f-7326-4e5e-951a-e8b5208d8ca5` completed with full
analysis and complete coverage, with no skipped files, against remote common
ancestor `a67c73b`. The isolated checkout excludes unrelated local work. Issue,
compliance, and skills reviewers ran; spec, UI, persona, and cross-repo skipped.
It returned three findings about older code in the submitted assistant baseline:

- `02adda60-9741-409f-8ccc-770693905962` [Reliability/action_required]: valid and
  fixed. Evaluation credit reservations already persist the evaluation ID as
  `OperationId`. Reclaim now releases that evaluation's abandoned reservations
  before retrying, including when pricing no longer permits evaluation. Both
  recovery and allocation run in transactions fenced by the owned evaluation
  lease. Reservation retries reuse a reservation after an uncertain commit.
  Provider calls remain outside transactions. Tests use real credit and monthly
  budget rows, preserve an unrelated reservation, and cover enabled/disabled
  pricing as well as existing late-worker and settlement-rollback cases.
- `20326ad7-aa26-4ddf-95c4-70fbf16596af` [Performance/remediation_recommended]:
  valid and fixed. Anki summary counts use SQL aggregates, while card details
  remain limited to the requested 100-card page. The two SQLite date mappings
  are covered with 1,001 cards and 2,000 reviews, exact counts, and assertions
  rejecting unbounded detail queries.
- `74f966e4-170f-4f9c-be66-b224998af2a0` [Correctness/remediation_recommended]:
  rejected. Both run controllers have API metadata. The registered
  `ApiProblemDetailsResultFilter` transforms error ObjectResults, including
  string bodies, into the shared Problem Details contract while preserving the
  detail. The existing real HTTP foreign-chat test verifies the 404 body, and
  the conflict test verifies HTTP 409 and its media type. No controller change
  is needed.

Targeted verification of these findings: **19 passed, 0 failed, 0 skipped**.
The final-placement fence also passed **42 tests**, with **7 SQL Server skips**.
The final full-suite rerun passed with the totals above. Qodo auto follow-up
`056818ee-fc75-415d-9d8d-433f00c8b21a` completed incrementally from checkpoint
`cc96c87bf9664145a170755ba8f8d67e`, with complete coverage, no skipped files,
and no new findings. Its 52 retained ledger entries have dispositions above or
in [the earlier review record](valid-findings-2026-10-11.md). The credit-recovery
and error-shape entries retain their original descriptions despite the verified
fix/rejection; this is not a claim that remote findings were dismissed. Reviewer participation matches
the initial review. All ten changed source/test files match the isolated tested
and reviewed snapshots.

DeepSource has no pushed revision for this local follow-up. Copilot/Greptile
remain unavailable under the temporary credits policy. No push, deployment,
or migration was performed.
