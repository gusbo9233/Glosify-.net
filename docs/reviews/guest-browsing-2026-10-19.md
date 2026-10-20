# Guest browsing verification — 2026-10-19

Baseline: `929674704282383694b6868a5bee882af1e34caf` (the current committed
UI-updates baseline, verified fetchable from origin). Review scope is the local
guest-browsing change; no changes were pushed or merged.

## Verification

- Affected .NET library, language, layout and authentication tests:
  **Passed: 559, Failed: 0, Skipped: 14**. The skipped cases require SQL Server.
  Command: `Demo__Enabled=false dotnet test Glosify.Tests --no-restore --filter 'FullyQualifiedName~GuestBrowsingTests|FullyQualifiedName~Authorization|FullyQualifiedName~ExternalSignInSecurityTests|FullyQualifiedName~Language|FullyQualifiedName~LayoutViewComponentTests|FullyQualifiedName~Anki|FullyQualifiedName~Quiz|FullyQualifiedName~Book'`.
- `npm test --prefix Glosify.ClientTests`: **391 passed, 0 failed**.
- The new database-backed browser journey compiles but is skipped without the
  configured browser-test host and SQL Server: **Passed: 0, Failed: 0, Skipped: 1**.
- Separate isolated Chrome checks against a local host passed: all five guest
  libraries and details; typing scoring; flashcard completion; login dismissal
  preserving practice; refresh resetting practice; mobile login and inline
  validation. The mobile dialog was visually inspected. Live external-provider
  sign-in was not exercised. HTTP tests cover password success, lockout,
  two-factor routing, and the existing external-provider account restrictions.
- The local demo-account seeder initially blocked tests by trying to connect to
  unavailable SQL. The new guest factory disables it explicitly; the broader
  command also disables it without changing application defaults.
- Tests caught and verified fixes for cookie-handler 302 status leaking into
  successful dialog JSON and unknown preview URLs falling through to an
  authentication challenge. Final Razor/browser-test compilation succeeded.

## Automated review

- Qodo operation `9c51795f-517d-4f22-acb9-90d438389c62` completed with complete
  coverage and no new findings on its submitted snapshot.
- Its historical open findings about Explore links, Home sections, sharing
  rejection/undo, and assistant placement predate this change. Current code
  already removes the links/sections, rejects retired proposals, preserves
  sharing undo, and handles missing placement targets. Its Anki duplicate-read
  finding concerns the unchanged authenticated path; guest requests return
  before that path. No unrelated changes were made for these findings.
- Final Qodo operation `f9c3f24a-cc48-420b-8dd2-2e74f3da2b38` failed with a
  managed-agent transport error. The first review does not certify subsequent
  login, routing, test, and presentation fixes. Final automated coverage is
  unavailable; those fixes were locally reviewed and verified as described above.
- DeepSource: unavailable for the unpushed working-tree revision. Obtain results
  for the actual PR head before merging.
- Copilot/Greptile: credits exhausted; temporary policy.

## Real feature UI follow-up

The user's follow-up replaces the separate preview templates with the actual
feature views and view models: transcript reader with speaker segments and
language streams, quiz workspace/settings/typing/flashcards, Anki study controls,
saved translation panels, and the PDF reader with an original two-page sample.
Sample models are immutable fixtures; practice remains browser-local. Account
mutations still prompt login, and private endpoints retain authorization.

- Affected HTTP tests: **162 passed, 3 skipped** (SQL prerequisites).
- Follow-up guest/language/layout/retired-route regressions: **133 passed**.
- Final client suite: **391 passed**.
- Database-backed browser journey compiled but remains skipped without SQL/host.
- Isolated Chrome verified all five actual feature screens, transcript stream
  switching, PDF page 2, quiz settings, typing scores, flashcard completion and
  restart, keyboard reveal/rating, and mobile transcript layout. Sample practice
  emitted no mutation POSTs. Final checks confirmed reverse pronunciation on
  the Spanish prompt and language navigation retaining the sample destination.
- Qodo `d402fba0-f8f1-4e8b-888e-2614701a7c57` completed with full coverage.
  Three valid findings were fixed: guest language now has a separate cookie,
  sample language navigation preserves its return URL, and reverse practice
  puts pronunciation on the Spanish side. Regression tests cover these fixes.
- DeepSource remains unavailable for the unpushed revision.
- Broader affected suite after the follow-up fixes: **571 passed, 14 skipped,
  0 failed**. Skips require SQL Server. This supersedes the earlier 559-pass run.
- Qodo `a850c474-58a5-4cfa-a5c3-8ac42a697881` completed and identified
  two further valid edge cases. The typing results Restart form now resets the
  local session, and guests ignore saved paid-provider preferences when resolving
  speech playback/settings. **42 guest HTTP tests and 392 client tests passed**
  after these fixes. Chrome confirmed the results Restart restores the answer
  input with no open login dialog and no mutation POST.
- Final Qodo `c87539e9-64e3-4d10-80d3-800b878d5623` completed with
  complete coverage and no new findings. This supersedes the earlier final-review
  coverage gap. Historical unrelated findings remain assessed as above.
