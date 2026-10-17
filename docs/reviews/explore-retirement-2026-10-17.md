# Explore retirement review

Baseline: `3868cbe16bc153f87394b33d39335a1f1b03cf19`, preserved on
`origin/archive/explore-before-retirement-2026-10-17`.

Qodo operation: `17cd7f3b-81f1-4f89-95b1-4d99da37fdf2` (completed, auto depth).
Issue review completed; compliance, skills, specification, UI, persona, and
cross-repository reviewers were skipped. Findings were assessed as follows:

- `efecafdc-0739-4411-9714-f5ba902973cf` (Correctness/action required), missing
  Home destination; `eb31aab6-e05f-4aec-9fab-ddf94a9b1d3b` (Reliability/action
  required), mismatched Home assertions: the first scoped submission omitted the
  previously authorized Home removal and therefore reviewed a synthetic tree.
  Current Home has only the hero and no Explore link. Include Home and its tests
  as dependencies in the final review; rendered-route tests pass.
- `3cf3ba01-9d64-4b61-b38a-76dc2e0dc90f` (Correctness/remediation recommended):
  accepted. The saved-message workflow now terminally rejects retired sharing
  proposals, in addition to the applier's whole-batch mutation guard.
- `61e40796-3080-4ebd-821a-7f534a7d690b` (Correctness/remediation recommended):
  accepted. Historical sharing journals retain a separate Undo dispatch path;
  new apply operations remain prohibited. Regression coverage includes both copy
  kinds and both visibility kinds.
- `37367c49-843f-4a12-af55-bb10fc67b05e` (Reliability/remediation recommended):
  intentional behavior change. New quizzes are created directly in owned folders,
  matching existing private-folder behavior; obsolete public flags no longer
  trigger deferred publication. The disappearing-destination test now explicitly
  seeds a legacy queued placement and confirms its failure notification survives.
- `229eed5a-7ea1-4fa2-a4f8-b68e3095b0b6` (Correctness/remediation recommended):
  accepted. Historical sharing/copy kinds remain in the client event filter so
  replayed metadata events do not invalidate unrelated study sessions.

DeepSource has not reviewed this local removal diff. Only the unchanged archive
baseline was pushed; no PR or merge was requested. Inspect DeepSource results and
latest Qodo coverage before merging a future PR.

## Final verification

Final Qodo operation `bcd1cf1f-fc86-421a-80f1-a9805b42c64a` completed with
zero findings and complete submitted-scope coverage. The issue reviewer ran;
compliance, skills, specification, UI, persona, and cross-repository reviewers
were skipped. This review included the actual Home removal and its tests as
required dependencies, but excluded the unrelated flashcard styling edits.

- Full .NET suite: passed 1,624; failed 0; skipped 48.
  Command: `Demo__Enabled=false dotnet test Glosify.Tests/Glosify.Tests.csproj --no-build --no-restore --verbosity quiet`
  The latest source had already been built by the focused run.
- Compatibility-focused run after Qodo fixes: passed 133; failed 0; skipped 0.
- Client learning-update tests: passed 25; failed 0.
  Command: `node --test Glosify.ClientTests/learning-updates.test.js`.
- `git diff --check` passed.

An earlier full run found an obsolete deferred-publication test and one voice
transcript timing failure. The former now explicitly seeds a historical queued
placement; both passed in an isolated 43-test follow-up and the final full run.
Demo seeding was disabled for test processes to avoid local demo SQL setup.
No browser visual check or deployment was performed.
