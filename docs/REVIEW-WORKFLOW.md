# Temporary review workflow

Use **DeepSource + Qodo from October 11 through October 25, 2026**, while
Copilot and Greptile credits are exhausted. Reassess credits on October 25;
this document does not schedule an automatic reset or change paid plans.
The owner authorized sending relevant Glosify source diffs, regression tests,
and implementation context to Qodo for this period. Exclude secrets and private
user data, and preserve unrelated working-tree changes.

DeepSource supplies static analysis of pushed code. Qodo reviews the intended
local diff with implementation context. Assess both sets of findings against
the actual code and tests. Neither replaces CI or human judgment.

Do not wait for new Copilot/Greptile feedback or request manual reviews from
them during this period. Existing integration settings are retained. In
particular, do not disable the entire GitHub ruleset named `Automatic Copilot
review`: it also contains deletion and force-push protections. Keep required
build checks, authentication, migration safeguards, and merge protections.

## Before submitting the change

1. Inspect the working tree and define the intended change. Run its relevant
   tests. For a dirty tree, identify every affected path explicitly rather than
   submitting unrelated work to review.
2. Fetch the target and compute the merge base; do not use the current target
   tip as an implicit substitute for the common ancestor.
3. Write a short Qodo context file explaining the problem, resulting behavior,
   compatibility constraints, and intentional decisions. Never include tokens,
   user content, or authentication configuration values.
4. Submit one final Qodo review, collect it, assess each finding, fix supported
   defects, and verify any changes. A submission ID is not a completed review.

Both CLIs are installed locally. If the shell cannot find them, their paths on
this workstation are `~/.local/bin/deepsource` and `~/.qodo/bin/qodo`.

Example from the repository root, in a clean or intentionally scoped checkout:

```sh
git fetch origin master
review_base=$(git merge-base HEAD origin/master)
review_dir=$(mktemp -d)
cat > "$review_dir/context.json" <<'JSON'
{
  "summary": "Describe the concrete problem and resulting behavior of this change.",
  "decisions": [
    "Describe compatibility constraints and why the implementation preserves them."
  ]
}
JSON
# Edit the context above to describe the actual change before submission.
qodo review --base "$review_base" --context-file "$review_dir/context.json" \
  --async --json > "$review_dir/submission.json"
review_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["operation_id"])' \
  "$review_dir/submission.json")
qodo review status "$review_id" --json > "$review_dir/result.json"
```

For a dirty checkout, append the complete intended set of file/directory
pathspecs to `qodo review`. Inspect the submitted paths and any exclusions.
Include changed or untracked dependencies needed to understand those files:
entities, services, fixtures, views, migrations, manifests, and configuration.
A narrow diff against an older base can otherwise describe a broken synthetic
tree even when the local application builds. Prefer a coherent checkout of the
intended PR; do not treat completed coverage of selected paths as a review of
the whole working tree. The [first Qodo review](reviews/qodo-2026-10-11.md)
documents this limitation and the resulting finding dispositions.
Exit code 2 from `review status` means pending; collect the **same operation**
again at a natural pause, respecting any retry delay. Preserve the operation ID
and collect within its retention window (currently one hour after completion).
Do not submit a fresh review to poll. Review the full result, coverage metadata,
and any still-open findings, not just an empty new-findings array.

Use default/auto depth for the final review. Reserve `--fast --async` for a
useful intermediate checkpoint; avoid one review per tiny edit, overlapping
reviews, or repeated unchanged submissions. Reuse completed coverage of an
identical snapshot. After supported fixes, test and review the changed work;
stop repeated passes that produce no actionable progress. Record service,
quota, or incomplete-coverage failures explicitly rather than claiming clean.

## Once a PR or commit has been analyzed

Inspect GitHub checks and DeepSource for the actual PR head. For example, with
the real PR number in `review_pr`:

```sh
review_pr=130
gh pr view "$review_pr" --json headRefOid,url
deepsource issues --pr "$review_pr" --limit 0 --verbose --output json
# Use the actual PR head SHA for the commit-level metadata check:
review_head=$(gh pr view "$review_pr" --json headRefOid --jq .headRefOid)
deepsource runs --commit "$review_head" --output json
```

These commands read existing analysis; they do not trigger it. DeepSource is
active for this repository with C#, JavaScript, Python, SQL, shell, and secret
analysis. However, on October 11 PR #130 had no DeepSource record. Older PRs
opened before activation may need a new event. DeepSource documents closing
and reopening such a PR to trigger analysis; do that only as a deliberate PR
action, not as a read-only check. New commits on PRs also trigger analysis.
[DeepSource FAQ](https://docs.deepsource.com/docs/platform/support/faq),
[PR analysis](https://docs.deepsource.com/docs/platform/getting-started/fix-issues).

Check that the result corresponds to the latest commit. Some CLI views can
fall back to the last completed run while a new one is pending; old data is not
approval of the new head. A missing PR/run, parser failure, or truncated export
is a coverage gap, not zero findings.
[CLI reference](https://docs.deepsource.com/docs/developers/cli/usage).

Use the existing [1,458-occurrence baseline triage](reviews/deepsource-2026-10-11/report.md)
to understand known noise and focus on newly introduced, relevant defects.
Do not require fixing the entire historical backlog for each PR, and do not
mass-suppress findings to turn a check green. The CLI v2.0.55 default-branch
export omitted nested occurrences even with `--limit 0`; large exports must
be reconciled with the API totals or dashboard. PR exports also have a result
cap, so do not infer completeness from the flag alone.

## Merge evidence and the end of the temporary period

Record the reviewed PR head, test results, DeepSource analysis status and
dispositions, and Qodo's completed operation/result and scope. Note unavailable
Copilot/Greptile reviews as `credits exhausted; temporary policy`. If an active
reviewer is unavailable, document the gap and perform local code review; do not
silently mark that reviewer clean or weaken existing required checks. No bot
comment is required by this workflow.

No production API key, GitHub Actions secret, paid-plan change, or new CI job is
needed for this local Qodo plus existing DeepSource process. The policy becomes
visible to collaborators when these documentation changes go through the usual
PR workflow. No remote reviewer settings were changed by this setup.

On October 25, confirm credits and reviewer availability with the owner before
restoring Copilot/Greptile as the expected pair or extending this policy. Their
configuration remains in place; do not treat the date as proof of renewal or
continue relying on this time-limited Qodo upload authorization afterward.
