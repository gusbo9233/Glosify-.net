# Analyzer validity review

**Remaining-review update:** All 146 pending behavioral findings have now been checked, with 191 PDF.js style preferences excluded as requested. Four additional problems were fixed. See the [complete follow-up dispositions and verification](remaining-verification.md). The original classifications below remain a historical snapshot.

**Fix update:** All 32 confirmed records were addressed locally after this review. See [fixes and verification](valid-fixes.md). The assessment below describes the pre-fix snapshot; current source probes now verify the repaired state.

Reviewed on 2026-10-27 against `064adc535487ff3a6ef71a528ffd18feaf10b564`. Application source was not changed.

All **3,844 exported records** are classified in [the CSV](validity-review.csv) and [the JSON](validity-review.json). This is a local validity review of existing reports, not a new scan. Rule-family classifications are explicitly distinguished from individually supported findings; 337 records remain follow-up questions, not confirmed defects or blanket dismissals.

## Findings worth acting on

| Priority | Current finding | Evidence and scope |
|---|---|---|
| Dependency maintenance | **15 advisories affecting two packages**: `undici 7.29.0` (13) and `sharp 0.35.2` (2) | [Glosify.CloudflareTranslator.Worker/package-lock.json](../Glosify.CloudflareTranslator.Worker/package-lock.json). Both are development dependencies in the Wrangler/Miniflare toolchain. Every current version matches a non-withdrawn advisory range. Update the compatible toolchain/lockfile to patched transitive versions; within these release lines, the exported advisory set requires at least undici **7.30.1** and sharp **0.35.5**. Production Worker exploitability was not established. |
| Error handling | **Unhandled rejection during PDF thumbnail cleanup** | [Glosify/wwwroot/js/books-library.js:54](../Glosify/wwwroot/js/books-library.js#L54) / line 62: `document.destroy()` can reject in `finally`, outside the rendering catch. The promise returned by `.finally()` is ignored. Executing the current module with a rejecting cleanup dependency reproduced an unhandled rejection. Catch/observe cleanup failure without interrupting queue accounting. |
| Accessibility | **8 controls lack explicit associated labels** (10 overlapping reports) | Quiz prompt, two quiz range sliders, quiz search, assistant text box, admin search, Credits and Note fields. Text placeholders may provide fallback accessible names, but are not persistent associated labels. The range sliders have no such fallback. Exact locations below. |
| Low-priority cleanup | **6 current cleanup findings** | Duplicate CSS background; labels used for two non-input displays; one extra JS call argument; two unused production parameters. These are not six demonstrated runtime failures. |

The dependency findings include **three provisionally ignored Semgrep records** whose locked versions remain affected. Provider status is preserved in the detailed data. Advisory versions were checked against the public advisory records, not only the exported severity: [Undici advisory](https://github.com/nodejs/undici/security/advisories/GHSA-xjj6-f33q-929c), [Sharp advisory](https://github.com/lovell/sharp/security/advisories/GHSA-wq5f-xc86-pv6w). All 15 IDs, ranges and patched versions are saved in [dependency-advisories.json](validation/dependency-advisories.json).

### Exact UI locations

| File/current line | Control |
|---|---|
| [Glosify/Views/Quiz/Index.cshtml:230](../Glosify/Views/Quiz/Index.cshtml#L230) | Read-only JSON-generation prompt textarea |
| [Glosify/Views/Quiz/Settings.cshtml:67](../Glosify/Views/Quiz/Settings.cshtml#L67) | WordRangeStart slider |
| [Glosify/Views/Quiz/Settings.cshtml:68](../Glosify/Views/Quiz/Settings.cshtml#L68) | WordRangeEnd slider |
| [Glosify/Views/Quiz/Settings.cshtml:92](../Glosify/Views/Quiz/Settings.cshtml#L92) | Quiz search input |
| [Glosify/Views/Shared/Components/AssistantPanel/Default.cshtml:86](../Glosify/Views/Shared/Components/AssistantPanel/Default.cshtml#L86) | Assistant message textarea |
| [Glosify/Views/Admin/AiCredits.cshtml:71](../Glosify/Views/Admin/AiCredits.cshtml#L71) | User search input |
| [Glosify/Views/Admin/AiCredits.cshtml:140](../Glosify/Views/Admin/AiCredits.cshtml#L140) | Credits input; visible label at line 139 has no for/id relationship |
| [Glosify/Views/Admin/AiCredits.cshtml:144](../Glosify/Views/Admin/AiCredits.cshtml#L144) | Note input; visible label at line 143 has no for/id relationship |

Use a real label association, or an appropriate accessible name where the design requires it; see [MDN accessible labeling guidance](https://developer.mozilla.org/en-US/docs/Web/Accessibility/ARIA/Reference/Attributes/aria-label). These were source checks, not an interactive assistive-technology audit.

### Cleanup locations

| File/current line | Current observation |
|---|---|
| [Glosify/wwwroot/css/site.css:3246](../Glosify/wwwroot/css/site.css#L3246) | Same hover block sets background twice; the later --surface-bright overrides --primary-fixed. |
| [Glosify.Translator.Extension/overlay/translator.html:20](../Glosify.Translator.Extension/overlay/translator.html#L20) | Translation label describes a div output, not a labelable form control. |
| [Glosify/Views/Quiz/Index.cshtml:332](../Glosify/Views/Quiz/Index.cshtml#L332) | Label describes static language output/hidden input. |
| [Glosify.LiveSubtitles.Extension/background/service-worker.js:122](../Glosify.LiveSubtitles.Extension/background/service-worker.js#L122) | sender is passed to handleMessage, which declares only message. |
| [Glosify/Services/Ai/Assistant/Runtime/AssistantSyncAdapter.cs:56](../Glosify/Services/Ai/Assistant/Runtime/AssistantSyncAdapter.cs#L56) | ResponseAsync does not use userId; its caller has already resolved an owned run. |
| [Glosify/Services/RealtimeTranslation/ScribeTranslationRelay.cs:330](../Glosify/Services/RealtimeTranslation/ScribeTranslationRelay.cs#L330) | TranslateAndSendAsync does not use storageWarning. |

## Findings in files updated since the scans

There are **664 active default-branch findings across 68 changed files**. Only **30 reported source ranges themselves were edited**; 634 ranges remain text-identical, including 302 whose line numbers shifted. A changed file is not sufficient reason to dismiss its findings. The [freshness comparison](findings-vs-current-files.md) contains the full mapping.

Among these 664 records, the confirmed actionable items are the quiz prompt label at current line 230, assistant text box label at current line 86, and static-language label cleanup at current line 332. The dependency findings, thumbnail rejection and other confirmed items are in unchanged files.

**Five records are stale:** three removed constant declarations (WorkspacePageSize, NewCardsPerDay, MaximumReviewsPerDay), plus two size/complexity reports attached to the former SyncCollectionAsync body. That body moved into SyncCollectionCoreAsync; the old metric does not describe the current short wrapper. This does not establish that the moved core is below its complexity threshold.

The main DeepSource/Codacy/SonarQube analysis baseline is `87a8e6c887a3a36b39496e116c5083d2ba643b10`. SonarQube analysis `430b569d-c2c1-41b5-a02e-8c85fff222ef` reports that revision. Semgrep also includes 8 PR-only findings and 10 historical fixed workflow findings; neither is silently counted as a current default-branch open issue. The three ignored dependency findings remain reviewed separately.

## Findings rejected or explained by surrounding code

| Reported concern | Current evidence |
|---|---|
| Missing CSRF protection | Program.cs installs a global AutoValidateAntiforgeryToken filter. API controllers use bearer auth; signed webhook and other explicit bootstrap/server endpoints have different boundaries. An absent action attribute alone does not prove missing protection. [ASP.NET Core antiforgery documentation](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0). |
| SQL injection | Reported production sites call EF Core SqlQuery with FormattableString parameters. [EF Core SQL query documentation](https://learn.microsoft.com/en-us/ef/core/querying/sql-queries). |
| PDF command injection | Process uses Environment.ProcessPath, UseShellExecute=false and ArgumentList; PDF data goes into generated files. |
| Unused parameters/properties | Two production parameter warnings ignore local-function captures (changes and quizId). Four property warnings ignore JSON deserialization or assignments in a partial class. |
| Assertion-free tests | Flagged tests use assertion helpers, EnsureSuccessStatusCode, throwing validators or deliberate successful-construction contracts. These are not automatically assertion-free tests. |
| JS parse errors / missing globals | ES modules and Chrome/AudioWorklet runtime globals are recognized by repository ESLint configuration. Local ESLint parsed 146 selected app/extension/test files with no errors or warnings. |
| Error-page information exposure | Exception details are restricted to IsDevelopment(); production uses a generic message. Deployment environment configuration was not audited. |
| Cookie Secure missing | The language-cookie helpers set Secure=request.IsHttps; trusted forwarding and HTTPS redirection precede endpoints. Actual reverse-proxy settings were not audited. |
| Hard-coded tokens | Three matches are a permission name, environment-variable read and TypeScript type declaration. Other reported literals are fixtures/placeholders or explicit local/ephemeral CI database credentials; no production secret was established at these sites. |
| Other floating promises | Inspected I/O is caught inside the called functions or handled with Promise.allSettled. The thumbnail cleanup case above is different and was reproduced. |

## Coverage and remaining uncertainty

Counts below are analyzer records, not deduplicated bugs. They include all branch/status scopes so they reconcile to the exports.

| Classification | Records | Meaning |
|---|---:|---|
| Confirmed behavior/accessibility issue | 11 | 1 error-handling record plus 10 overlapping label reports for 8 controls. |
| Confirmed affected dependency | 15 | Affected version established; production exploitability not established. |
| Confirmed cleanup | 6 | Current unnecessary/duplicate code or markup; low impact. |
| Claim contradicted by source/context | 288 | Evidence recorded per finding/group. |
| Intentional boundary or behavior | 103 | Explicit scope/contract; deployment assumptions remain noted. |
| Intentional test construct | 49 | Helpers, smoke contracts, stubs and controlled fixture regex. |
| Analyzer configuration/dialect problem | 177 | Module parsing, missing environment globals, SQL/Bash dialect, invalid Stylelint rule, test discovery. |
| Convention/cleanup suggestion | 2,475 | Grouped by rule; not individually approved refactors or demonstrated bugs. |
| Size/complexity metric | 368 | Not remeasured; not a behavioral defect by itself. |
| Hardening/contract/performance follow-up | 55 | Specific uncertainty recorded; neither confirmed defects nor blanket false positives. |
| Asset-generation environment follow-up | 48 | Requires pinned Blender/NumPy assets; generation was not run. |
| Vendored PDF.js follow-up | 234 | Minified diagnostics not individually validated; investigate upstream version/advisories, not ad hoc bundle edits. |
| Stale location/measurement | 5 | Removed declarations or moved method body. |
| Already fixed per provider | 10 | Historical Semgrep workflow records. |

The **337 follow-up records** comprise 55 hardening/contract/performance questions, 48 asset-pipeline reports and 234 vendored PDF.js reports. Examples: regex timeouts for escaped literal word matching, bounded chat sentence parsing, private lock encapsulation, CI lifecycle-script policy, supported switch kinds, and whether omitting AutoSaveConversations should reject the request instead of disabling the preference. These require a concrete contract, environment or reproduction before promotion to confirmed defects.

## Verification

- Current app and test projects built successfully during the focused .NET run. Two focused runs: **287 passed, 0 failed, 8 skipped**. The skipped cases require SQL Server.
- Selected JavaScript tests: **48 passed, 0 failed** (extension packaging, subtitle buffer, book sentence segmentation).
- ESLint: **146 files, 0 errors, 0 warnings** using repository configuration.
- Source probes reproduced the thumbnail unhandled rejection and verified TTS argument forwarding, label markup, duplicate CSS and constrained redirect-regex behavior. Run `node october-analysers/validation/current-source-probes.mjs` from this checkout.
- Every dependency record was checked against the actual lockfile and live public advisory ranges; the 15 checks passed.

[Verification summary](validation/verification-summary.json), [probe results](validation/source-probe-results.json), [portable probe](validation/current-source-probes.mjs). Full browser journeys, live deployment/proxy configuration, SQL Server tests, Blender generation and new external analyzer scans were not run. Focused passing tests do not establish a clean security audit.
