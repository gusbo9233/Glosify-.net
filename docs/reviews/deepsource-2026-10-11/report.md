Reviewed **all 1,458 open DeepSource occurrences across 80 rules**. The recommended work is four focused source changes plus analyzer configuration/triage cleanup. A blanket autofix or a campaign to reach zero warnings would spend most effort on naming and style and would introduce risk in several correct code paths.

| Decision | Occurrences | Meaning |
|---|---:|---|
| Fix source behavior | 10 | Four small tasks; includes issues uncovered while checking the reported rule. |
| Investigate/fix analyzer configuration | 122 | Valid modules, Chrome APIs and worklet declarations misinterpreted by analysis. Reanalyze afterward. |
| Defer | 130 | Optional maintainability, robustness, or typing work when the feature changes. |
| No source change now | 1196 | Verified false positives, intentional behavior, generated code, or cosmetic conventions. This is not an instruction to bulk-suppress all these warnings. |

| Analyzer | Occurrences | Rules |
|---|---:|---:|
| csharp | 895 | 33 |
| javascript | 459 | 25 |
| python | 82 | 17 |
| sql | 11 | 3 |
| secrets | 10 | 1 |
| shell | 1 | 1 |

The default branch is `master`, and DeepSource's latest completed analysis is commit [`ef9cc38e6806531f7cd586e24c04460b2384f79e`](https://github.com/gusbo9233/Glosify-.net/tree/ef9cc38e6806531f7cd586e24c04460b2384f79e), finished on 2026-10-11 at 17:49 UTC. Source review targets an isolated archive of that exact commit; each detailed report distinguishes local checks from isolated reproductions. The current working branch, `assistant-v2`, differs substantially and has no DeepSource run. A file missing locally was therefore not dismissed as stale when it still exists on analyzed master. The four recommended source changes also apply to code present in the current working tree.

The CLI's `issues --default-branch --limit 0` returned only 897 occurrences. Its current source paginates rule groups but not the occurrences inside each group. A read-only request to the same DeepSource API using the existing CLI authentication retrieved 15 pages: 1,458 unique IDs, exactly matching the API's `totalCount`. The missing 561 were 553 async-naming occurrences and 8 JavaScript parser occurrences. This is a complete backlog export, not a sample. The [API exposes occurrence pagination](https://docs.deepsource.com/docs/developers/api/repository).

The [occurrence ledger](issues.csv) contains one row per original occurrence, including repeated rule/path/line combinations with distinct IDs. The detailed [C# review](csharp.md), [JavaScript review](javascript.md), and [Python/shell review](python-shell.md) explain the grouped decisions. Classification is at the rule/context level with individual exceptions; it is not a claim that every finding received an independent execution test.

| Priority | Recommended work | Reason and acceptance check |
|---|---|---|
| P1 | Restrict the development SQL port to loopback | `docker-compose.dev.yml:17,25` combines a deliberately public development password with `1433:1433`, which omits a host IP. Change the mapping to `127.0.0.1:1433:1433`. Confirm normalized Compose output specifies `host_ip: 127.0.0.1` and local development still connects. This is development-host exposure, not a discovered production credential leak. |
| P2 | Reject overflowing cache-token counts | `Glosify/Controllers/GameUsageController.cs:20-25` bounds input/output counts but adds unbounded cached/cache-write `long` values. A test using the actual HTTP endpoint with a valid service key, `InputTokens=100`, and both cache counts at `long.MaxValue` returned 204 and persisted the event. Bound each count before arithmetic, or use a subtraction-based comparison after bounds checks. Regression: malformed values return the shared 400 Problem Details response and persist no usage/session rows; valid boundary cases still succeed. |
| P2 | Eliminate duplicate delete confirmations | `site.js:143-148` and both `quiz-details.js:31-33` and `anki.js:23-27` handle the same `data-confirm` submission. A browser fixture executing the actual scripts reproduced two dialogs per submit, including after cancellation. Keep one confirmation owner. Regression: one dialog per action; cancellation submits nothing; acceptance submits once. Replacing every native dialog is unnecessary for this defect. |
| P2 | Make release-smoke validation execute under Python optimization | Five assertions in `scripts/verify-fractional-tts.py` are reported by `BAN-B101`; the script contains six, and all six should be converted. These checks gate reopening ingress during deployment. Replace them with explicit failures and useful non-sensitive diagnostics. An isolated reproduction showed invalid data failing normally but checks disappearing at optimization level 2. Current CI uses ordinary `python3`, so this is hardening against optimization, not proof of a currently bypassed deployment gate. Test mocked invalid inputs both normally and with `PYTHONOPTIMIZE=1`. |

The overflow was discovered while inspecting a complexity warning, the duplicate dialog bug while inspecting native-confirm style warnings, and the network exposure while inspecting an intentional development credential. These are concrete surrounding-flow defects; DeepSource did not specifically diagnose their root causes. Fix the demonstrated behavior rather than mechanically applying its suggested style changes.

Docker Compose normalization confirmed that the current port mapping has no `host_ip`. Docker documents that an omitted host address publishes to all host interfaces by default; actual LAN reachability on this machine was not tested. The mapping should encode the repository's stated local-only intent independently of host/firewall defaults. [Docker port-publishing documentation](https://docs.docker.com/engine/network/port-publishing/).

Improve analysis configuration before spending effort on cosmetic changes. All 108 `JS-0833` files passed a real Node.js module syntax check, and the checked-in ESLint configuration already selects modern module syntax and declares `chrome`. Audit DeepSource's effective dashboard/configuration ingestion, module parsing, test-file classification, and browser/worklet globals, then rerun. Adding `module_system = "es-modules"` alone is not a proven fix: it is already the documented default. Preserve valid module syntax and keep syntax checking enabled. DeepSource documents its [module and globals settings](https://docs.deepsource.com/docs/platform/reference/core-analyzers).

Of 653 `CS-R1073` naming findings, 649 are test methods (620 unit/integration tests and 29 browser tests), and only four are production helpers. Defer renaming those helpers until nearby substantive work; do not rename hundreds of readable test cases solely to add `Async`. Record a test-scoped rule decision if the team chooses that convention.

There is no justified blanket emergency fix for the 48 findings labelled CRITICAL. Reviewed examples include used-through-disposal lock leases, correctly shared lock objects, EF `byte[]` rowversions, integration-test access to partial `Program`, and a nullable result returned *inside* an async lambda rather than a null Task. The eight Python critical findings concern a read-only default dictionary and `next` in ordinary asset-builder functions. Fixing severity labels without reading the call path would be counterproductive.

All ten secret findings were inspected. They cover a browser-storage key name, dummy redaction-test tokens, documentation placeholders, and the intentionally known local SQL credential. No real production credential was established by these ten findings. Preserve secret scanning; dismiss only the exact verified examples. Address the development SQL binding described above before treating that context as fully resolved.

All eleven SQL findings were inspected in `docs/analytics/assistant-analytics.sql`. Five undefined-reference warnings refer to aliases declared by the same query's `OUTER APPLY` or `LEFT JOIN` clauses; three missing-alias warnings concern later `UNION ALL` arms whose column names come from the first arm. Dismiss those eight. Defer three alias/identifier naming preferences. This is supported by the query structure and [Microsoft's T-SQL FROM/APPLY documentation](https://learn.microsoft.com/en-us/sql/t-sql/queries/from-transact-sql?view=sql-server-ver17); the query was not executed against SQL Server during this review.

Do not apply the NumPy suggestion to replace `len(changed) == 0` with generic truthiness: `changed` is an array, so that rewrite can break behavior. Preserve the Bash-only loopback validator in the Bash script, intentional exception boundaries, defensive collection copies, and framework-required signatures. Keep complexity findings as maintenance signals; extract and test cohesive behavior when a real change requires it, rather than splitting functions only to reduce a number.

No application code, analyzer setting, suppression, or remote issue state was changed. Only this review's documents and occurrence ledger were added to the working tree.

Verification: the analyzed snapshot built successfully; **60 focused existing C# tests** and **54 focused existing JavaScript tests** passed, with no skipped cases in those runs. A separate HTTP integration test reproduced the overflow and intentionally asserted its incorrect acceptance. Real Chromium DOM fixtures reproduced duplicate confirmation calls. All **108 flagged JavaScript files** passed module syntax checks on local Node 26.7 (CI targets Node 24). Isolated Python compilation demonstrated assertion removal, Bash syntax/loopback URL checks passed, and local localization data checks passed.

The full application suite, SQL Server integration suite, Blender builds, analyzer-specific Python type checks, actual LAN reachability, and live provider/production checks were not run. NuGet's vulnerability feed was unavailable during cached restore; this review is not a dependency-vulnerability audit. Parser-configuration remediation still needs a new DeepSource run, which can reveal additional findings previously hidden by parse failures.

The review-only [overflow reproducer](evidence/DeepSourceTriageProbeTests.cs.txt) is preserved as a text artifact; it asserts the current bug, not the desired fixed behavior. No assertion from that proof should be copied unchanged into a permanent regression test.
