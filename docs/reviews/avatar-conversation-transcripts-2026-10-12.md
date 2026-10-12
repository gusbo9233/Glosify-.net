# Avatar conversation context and transcript verification — 2026-10-12

Implemented locally on `codex/avatar-conversation-help` in the
`Glosify-avatar-language` worktree. Existing floating-chat and word-tabs changes
were preserved. Nothing was pushed or deployed.

## User findings and resulting behavior

All four reported bugs were confirmed. The remaining items were inspected in
context and corrected where supported by the code.

- Native Space activation is preserved for checkboxes, radios, buttons, editors,
  and dialogs. The shared speech-settings dialog is rendered by the layout, so
  it was affected even though it is absent from the Avatar view itself.
- Unsaved partner, quiz, New conversation, and same-tab navigation transitions
  offer Save / Discard / Cancel. Save/status failures retain the source. Cancel
  keeps the current session paused. Refresh/close uses the browser warning;
  settings explain the eight-hour inactivity and server-restart limits.
- Ending or losing live memory no longer traps the assistant in repeated 404s.
  Previously submitted sources remain usable only in the same owned chat and
  language. Unsubmitted missing context is cleared with a recoverable error.
  Reload preserves opaque conversation-to-chat associations in tab storage.
- Undo accepts the exact legacy three-field create snapshots while preserving
  current-format, ownership, and edited-content safeguards.
- Voice replies and playback acknowledgements do not await transcript writes.
  A bounded, coalescing background queue saves finalized turns, drains on close,
  and uses per-owner synchronization. Disabled autosave needs no database access
  on a turn; unrelated owners and subtitle deletes avoid the avatar save lock.
- Immutable assistant sources are stored separately from run checkpoints,
  deduplicated within a chat, and charged to existing storage quotas. Memory
  capacity reclaims only fully saved idle archives and has an aggregate text
  bound; it refuses new work rather than evicting unsaved turns. A rejected new
  start leaves the current call and archive writer intact.
- Flashcard freshness loads one owned card and compares text exactly in managed
  code, preserving case-sensitive and sentence-normalization behavior. Stale
  requests remove their own session. Legacy Skip requests remain compatible.
- Transcript source and spoken-turn labels use all existing display locales,
  including singular/plural forms.
- Completed paid evaluations enter a durable settlement state with an independent
  timeout. A failed debit retries original usage without paying the provider
  again; failed cleanup also remains recoverable instead of stranding credits.
- Transcript kinds use shared constants; repeated learning-change bookkeeping
  uses one helper; avatar preference persistence lives in a service.

Additional supported review findings were fixed: per-direction Anki exclusions,
pre-approval card/item validation, deleted-quiz navigation, duplicate terminal
assistant renders interrupting history loads, out-of-order transcript status, and
practice links that would fail under the current saved learning language. Successful
transcript persistence is also separate from status delivery: normal socket shutdown
no longer produces a misleading save-failure warning.

## Verification

- Final complete .NET suite with `RUN_SQLSERVER_TESTS=true`: **1,559 passed,
  7 skipped, 0 failed**. All 39 SQL opt-in tests ran against Colima. The seven
  skips are live-provider checks.
- Complete client suite: **309 passed, 0 failed**.
- SQL coverage includes concurrent transcript save/delete/resave, snapshot
  quota/dedup/chat cleanup, case-sensitive flashcard freshness, durable paid
  evaluation recovery, accounting, billing, and existing migration regressions.
  The historical fractional-credit migration fixture now seeds the old schema
  directly; its ledger fingerprint and downgrade assertions remain unchanged.
- Full EF migration chain applied to an isolated Colima database;
  `has-pending-model-changes` reported none. The migration database was removed.
  The SQL helpers create and remove isolated databases; existing development
  databases were not modified.
- Real Chrome with an isolated loopback AvatarFixture host and simulated providers:
  native keyboard controls; Save / Discard / Cancel for partner, quiz, and New;
  failed-save retention; navigation cancellation; unused assistant-source cleanup;
  and Ask → save → reload → Ask reusing the same assistant chat all passed.
  Mobile dialog fit, menu/settings separation, settings reopening after scroll,
  and Save before an actual Polish → Swedish language switch also passed.
  The dedicated browser session and isolated fixture host were closed.
- Existing integration coverage creates an owned quiz from an archived avatar
  conversation through the assistant tool workflow. Browser checks use
  `scripts/avatar/browser-transcript-review.js`, `browser-help-review.js`, and
  `browser-retention-review.js`.
- `git diff --check` passed.
- No paid provider calls were made. Live provider behavior is not certified by
  fixture or unit tests. No production migration was run.

## Qodo coverage and dispositions

Qodo rule searches returned no applicable rules. Uploads contained relevant
source, regression tests, and branch dependencies against merge base
`e8a12e5c97d4e519bfdad2454d9cf0a9d6eb8f3f`; secrets and browser session data were
excluded. No remote comments or stored dismissals were posted.

Completed auto-depth operation **9651a6ee-a060-4119-8e6a-5a1198c9baf6** reported
complete coverage of its submitted scope (checkpoint sequence 8079,
`b073866591506cfc28c628753a68afce1d816d2c`). It supplied six new findings and 16
older open findings. Their categories/levels below are Qodo's; dispositions are
our code-based assessment. Short IDs identify the full references in the result.

| Reference | Category / reported level | Disposition and evidence |
| --- | --- | --- |
| `1502f53d` | Correctness / action required | Fixed legacy Skip HTTP compatibility, including unrevealed cards; current UI has rating controls. Controller regressions cover both. |
| `f41d8579` | Correctness / action required | Fixed: Cancel leaves the session available and paused. Retention tests and Chrome cover it. |
| `210cd92e` | Reliability / recommended | Fixed: unknown status cannot bypass same-tab navigation protection. |
| `e2735501` | Reliability / action required | Queue-loss claim rejected: turns are recorded before notification, and a full pending slot's later snapshot includes those turns. After dequeue a new slot is available. Backlog/shutdown test covers six turns. Separately moved notification before caption transport so disconnect cannot skip it. |
| `e5ca4b46` | Reliability / recommended | Fixed: AssistantAvatarSnapshot participates in resource accounting; SQL regression checks quota rejection, deduplication, and deletion. |
| `a255e346` | Security / recommended | No revocation change: accepted snapshots are durable user-owned assistant material, like saved transcript/chat content. New live capture still checks preview access; reads require owned thread and language. Role changes do not confiscate previously shared material. |
| `5339109e` | Correctness / informational | Already addressed: mode changes clear both Space flags; release only ends the hold initiated by that key. |
| `04363b0b` | Correctness / informational | Stale claim: websocket help options reach the structured reply path; server tests and fixture browser checks cover translations/suggestions. |
| `842ba368` | Correctness / recommended | Already addressed: cleared-turn and chat revision checks suppress late captions/transcripts without skipping protocol acknowledgements. |
| `e06173c6` | Correctness / recommended | Already addressed: settings control uses the drawer's trailing edge, clear of the shared menu. Mobile browser checks cover geometry. |
| `e544a3fc` | Correctness / recommended | Existing preserved fix delegates tab clicks, so replaced quiz content works; client regression passes. |
| `a8f84027` | Correctness / recommended | Already addressed: opening settings scrolls the drawer into view. |
| `cde983b1` | Correctness / recommended | Fixed: stale card removal uses the owned session ID rather than matching settings. |
| `6ac89ed4` | Correctness / action required | Fixed: changing Anki direction preserves each existing card's exclusion; bidirectional regression coverage. |
| `59ca7c07` | Correctness / recommended | Fixed: deleted-quiz refresh follows only the expected same-origin quiz-library redirect. |
| `cbe07e06`, `5ff05e50` | Correctness / recommended | Fixed: invalid/missing/mismatched Anki IDs and directions fail before approval; omitted item type retains the documented words default. |
| `ac53f6f7` | Correctness / recommended | Fixed: duplicate terminal run renders no longer invalidate pending history reloads. |
| `d31a7f56` | Correctness / recommended | Fixed: server status revisions and client GET generations reject older results while accepting later deletion state. |
| `8db4b51d` | Reliability / action required | Fixed: abandoned-reservation cleanup failure retains a retryable lease/state. |
| `769edd40` | Correctness / recommended | Fixed with native Space handling, including chat and shared-dialog buttons. |
| `eca97d07` | Correctness / recommended | Fixed: bounded, validated opaque associations in sessionStorage preserve the chat across reloads; no transcript text is stored there. |

Early review attempts `3b91f95f-aa4d-4dc1-960e-3783ba5b0f0d` and
`8c0d567e-2f0e-466e-8f76-c7cfdd94e6d0` failed transport. Those failures are
superseded by the completed review above, not treated as clean results.
Follow-up auto-depth operation **d07e4b2d-3754-4be6-9c1d-f1a57dd89773**
completed (checkpoint sequence 8084, `3fbd831bae95c4e922c1f69692c514bd10c5456a`)
with complete coverage of its submitted paths, three new findings, and 22 carried
findings already assessed above. This exposed two omitted branch dependencies in
the review's reconstructed tree; local code already includes both integrations.

| Reference | Category / reported level | Disposition and evidence |
| --- | --- | --- |
| `e1f2f0ca` | Correctness / action required | Incorrect for this checkout: `assistant/runs.js` accepts `onChanges` and invokes it for fresh change revisions and forced refreshes. Its branch diff was missing from the upload; include it and the existing assistant-runs tests in final coverage. |
| `6320f27c` | Correctness / action required | Incorrect for this checkout: `_AppLayout.cshtml` renders both learning-screen wrappers and loads `learning-updates.js`. Its branch diff was missing from the upload; include the layout and related views in final coverage. |
| `43cb4df8` | Correctness / recommended | Fixed: links use the current saved app language for Anki, flashcards, and typing; nine regressions cover changed selection, ownership, aliases, and the routes' existing no-selection behavior. Cross-language management remains available. |

The historical migration test seed was corrected after that review snapshot;
it changes no production behavior and passed its SQL migration regression.
The practice-link correction and complete branch dependencies were included in
completed auto-depth review **215ed610-73a2-4ada-aedd-c808ef486bcc** (checkpoint
sequence 8088, `54ca96fd5e62c7d92e91d6ff31d9692bcbf92c31`). It reported complete
coverage of the 171 submitted paths, **no new findings**, and 25 carried findings
assessed in the tables above. The issue, compliance, and skills reviewers ran;
spec, UI, persona, and cross-repository reviewers did not run.

The subsequent combined suite caught one notification/shutdown race: a successful
save followed by cancelled status delivery was logged as a save failure. Storage
and transport handling are now separate; the strict no-warning assertion remains.
All 16 voice tests pass. Completed auto-depth review
**f4576bc6-0da2-4186-a6e5-2e9e1600d5b4** (checkpoint 8090,
`f6ac56865d6293ea8965cc9b13ec6063dfdccdeb`) reported complete coverage and one
new finding, `f5fcc827-304b-48b3-a32b-da63ef185cb4` (Reliability / recommended):
a rejected new call could cancel the current call before the dirty-memory capacity
check. Confirmed and fixed by preflighting admission before cancellation or writer
transfer. Six regressions preserve the old call on rejected new/mismatched context,
allow a later valid resume, and retain replacement behavior at the live-session cap.
The final admission correction was covered by completed auto-depth review
**f5ffb248-4b09-46e3-a90b-9287918a87de** (checkpoint 8092,
`3d283898fab2463ed56870a7510821234e4cd7af`). It reports complete coverage,
**no new findings**, and 26 carried findings, all fixed or assessed above.
No reported files were skipped. The final combined suite then passed all 1,559
non-provider tests, including all 39 Colima SQL tests. Only this verification
record was updated afterwards; production code is the reviewed snapshot.

DeepSource has no result for this unpushed working tree. Obtain its current
PR/commit results before merge. Copilot/Greptile were not requested under the
temporary credits policy. Local verification does not stand in for merge checks.
