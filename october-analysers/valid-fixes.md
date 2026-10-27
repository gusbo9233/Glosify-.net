# Confirmed analyzer fixes

All **32 confirmed records** from [the validity review](validity-review.md) are addressed in the local working tree based on `064adc535487ff3a6ef71a528ffd18feaf10b564`. Provider findings have not been changed remotely. The original review remains a snapshot of the code before these fixes.

| Finding group | Records | Fix |
|---|---:|---|
| unused production parameters | 2 | Removed unused userId from ResponseAsync and storageWarning from TranslateAndSendAsync, updating their private callers. Ownership lookup and the storage-warning state used by transcript persistence remain in place. |
| worker dev dependencies | 15 | Updated Wrangler to ^4.153.0; Miniflare 5.20261023.0-beta resolves Undici 7.30.1 and Sharp 0.35.5, outside all 15 exported affected ranges. |
| duplicate css background | 1 | Removed the earlier overridden background declaration and retained the effective --surface-bright hover color. |
| output label | 1 | Replaced the form label for translator output with a styled text span; associated the keyboard-focusable result region with that visible name. |
| form labels | 10 | Added explicit associated labels or ARIA names for all eight controls; linked slider descriptions and supplied distinct range names in every supported display language. |
| display label | 1 | Replaced the form label for static learning-language output with a span using the same form-label styling. |
| unused extra argument | 1 | Removed the unused sender argument when invoking the subtitle handleMessage function; retained the Chrome callback signature. |
| thumbnail cleanup rejection | 1 | Catch PDF-worker teardown failures in renderPreview finally; preserve rendered/error state and release the concurrency slot. Two runtime regression cases also verify queue drainage. |

The supported toolchain update follows [Wrangler 4.153.0](https://github.com/cloudflare/workers-sdk/releases/tag/wrangler@4.153.0); the resolved lockfile supplies the patched dependencies without overrides. This addresses the exported advisory set, not a claim that every dependency is free of vulnerabilities. [Per-advisory checks](validation/dependency-fix-checks.json).

## Verification

- **224 .NET tests passed**, including rendered view labels, localization catalog parity, assistant runs and translation relay checks; **6 SQL Server variants skipped** because SQL Server was not configured.
- **563 JavaScript tests passed**: 416 application tests, 45 translator-extension tests and 102 subtitle-extension tests.
- PDF cleanup tests passed for successful and failed rendering while teardown rejects, including queue concurrency/drainage. Both tests fail against the original code with unhandled promise rejections.
- **1 translator browser flow passed** using the real extension and a local mock server. The result is found by its accessible role/name, then translation and save are verified. The first runnable attempt timed out waiting for translated text; the original baseline and subsequent fixed-tree run passed. This transient failure is recorded rather than treated as a consistently green run.
- ESLint passed for **147 files**, with zero errors/warnings.
- Worker TypeScript check and local Wrangler dry-run build passed. The dry run did not deploy anything.
- All 15 saved advisory ranges were checked against the new lockfile; no matching locked package remains in an affected range.
- Source probes now validate the repaired behavior. [Original probe results](validation/baseline-source-probe-results.json) are retained separately from [current results](validation/source-probe-results.json).

The Node-based checks ran on local **Node v26.7.0**; extension CI specifies Node 24. Full browser suites and production services were not exercised. The diff received local review; Greptile review has not run for these local, unpushed changes.

[Per-finding fix mapping (CSV)](valid-fixes.csv) · [Detailed checks and source hashes (JSON)](valid-fixes.json). The 337 earlier follow-up records remain unconfirmed and outside these fixes.
