# Remaining analyzer verification

Verified all **146 remaining behavioral findings** against `064adc535487ff3a6ef71a528ffd18feaf10b564` plus the working-tree fixes. The **191 PDF.js style preferences** were excluded as requested. Every one of the 337 previously pending records now has an individual disposition in [JSON](remaining-verification.json) and [CSV](remaining-verification.csv).

## Four additional problems fixed

1. **Quiz sessions could disappear from resume lookup.** A request waiting on an old, emptied per-user list removed a replacement dictionary entry. `QuizSessionRegistry.RemoveUserEntryIfEmpty` now removes only the matching key/value pair. Deterministic FindActive and Deregister cases failed before and pass after.
2. **Flashcard responses could mix states from concurrent requests.** Reveal built its response after releasing the session lock. A concurrent rating yielded an old card with an advanced index and score; reading the index twice also permits an out-of-range lookup on the last card. `BuildViewModel` now uses the same session lock. A controlled two-thread regression failed before and passes after.
3. **PDF.js teardown could emit unhandled rejections.** Both Fetch reader cancel methods ignored rejection from an already errored response body. Added two small handlers, documented in [the vendor README](../Glosify/wwwroot/lib/pdfjs/README.md). Eight tests execute the shipped classes; both error cases failed before. The existing read error still propagates and fetch still aborts. Original bundles matched the upstream 4.10.38 release byte for byte; the only display-bundle differences now are these two handlers.
4. **The localization helper had quadratic matching on malformed model output.** Replaced overlapping digit/format matching with one scan per closing-brace segment. An 8k-digit unclosed placeholder dropped from about 289 ms to 0.007 ms; a 64k-digit input takes about 0.026 ms. Existing placeholder extraction is preserved for 8,148 resource values and 10,000 randomized strings.

The lock rules were too broad: their suggested lock-target concern was not the demonstrated bug. Following those request flows exposed the two concrete races above.

## Dispositions

| Outcome | Records |
|---|---:|
| Reported defect fixed | 3 |
| Verified API behavior; contract decision | 1 |
| Verified input or execution boundary | 27 |
| False positives / intentional behavior | 94 |
| Optional CI/dependency policy | 4 |
| Adjacent defect found and fixed while checking the warning | 4 |
| Style preferences excluded | 191 |
| Feature not used by this app | 8 |
| Upstream concern outside current app integration | 5 |

## Important decisions

- **Assistant actions:** allowlists and owned server-stored proposals prevent unsupported actions reaching the switches. End-to-end unknown-action probes returned tool errors and no mutations. Other switch warnings were checked against their filtering/recursion/test-fixture roles.
- **Word matching and PDF sentence splitting:** metacharacters are escaped in the C# patterns; both implementations matched literal input and processed one-million-character mismatches without pathological growth. The actual PDF sentence fallback processed a million characters in about 2.3 ms.
- **Subtitle matching:** the regex has a bounded 800-character input in every production matching path. Large incoming deltas are truncated before matching. No unbounded caller was found.
- **Avatar preferences:** an authenticated, antiforgery-valid `POST {}` currently disables autosave. This was reproduced through HTTP after first enabling it. The UI always sends the boolean. Requiring an explicit field would change the API contract; no such change was made.
- **PDF.js:** all 43 behavioral reports were checked separately, including exact minified offsets and readable upstream source. Fixed the two reachable cancellation reports. Real problems in the unused filename helper, XHR header fallback, form editing, and ML alt-text editor remain identified as upstream concerns rather than blanket false positives. The app uses Fetch, workers, canvas rendering and TextLayer, not these editor APIs.
- **Asset scripts:** all 48 reports were traced. Real Blender/NumPy probes confirmed positional array/reshape calls, synchronous closure execution, material nodes, unmodified default dictionaries, and fail-fast GLB guards. Required asset lookups intentionally fail if a pinned offline source package is incomplete. The full avatar assets were not rebuilt.
- **Local subprocess/CI rules:** no shell input injection path was found. Existing lifecycle-script inventory has no script executed by the cited Ubuntu npm installs; npx uses the installed pinned Playwright. Cooldown/ignore-scripts remain policy choices.

## Verification

- .NET: **167 passed, 7 SQL Server tests skipped**, including five temporary HTTP/assistant/regex investigation probes archived in [RemainingAnalyserVerificationProbes.cs](validation/RemainingAnalyserVerificationProbes.cs). Per-test results: [JSON](validation/remaining-dotnet-results.json).
- Client JavaScript: **424 passed**. Python release/localization tests: **10 passed**. ESLint: **148 files, zero errors/warnings**.
- Blender **5.2.1 LTS**, NumPy **2.3.4**: [helper probe results](validation/remaining-blender-results.json).
- Chromium **151.0.7922.34** loaded and rendered a real PDF with the shipped worker, extracted `Hello PDF`, then destroyed it with no page errors: [results](validation/pdf-browser-results.json).
- [JS probes before](validation/remaining-js-before.json) / [after](validation/remaining-js-after.json); [localization before](validation/localization-regex-before.json) / [after](validation/localization-regex-after.json).
- Negative controls: [registry races](validation/registry-before.log), [response race](validation/remaining-probes-before.log), [PDF cancellation](validation/pdf-cancellation-before.log).

## Limits and provenance

Local Node is 26.7.0; CI uses Node 24. SQL Server-only tests still require CI. No production deployment, data mutation, analyzer re-run, or external source upload occurred. These local changes have not received Greptile review.

The PDF cancellation behavior follows the [Streams Standard](https://streams.spec.whatwg.org/#readable-stream-cancel). The [upstream fetch implementation](https://raw.githubusercontent.com/mozilla/pdf.js/master/src/display/fetch_stream.js) still ignores these rejection results as checked on 2026-10-27. The registry fix uses the documented [key/value removal overload](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.tryremove?view=net-10.0), which requires both to match.

The original [validity review](validity-review.md) and [32-fix report](valid-fixes.md) are preserved as earlier snapshots. This report resolves their pending behavioral review queue; it does not claim every unused upstream code path or deployment environment has been exhaustively tested.
