# JavaScript DeepSource triage

Analyzed every one of **459 occurrences across 25 rules**, including all 108 JS-0833 results after pagination. Source of truth is the analyzed default-branch commit **ef9cc38e6806531f7cd586e24c04460b2384f79e**, extracted read-only under `default-source/`. The user's current assistant-v2 branch diverges, so absent local avatar files are not treated as stale findings. No production files, tests, DeepSource configuration, issue status, or working-tree changes were made.

## Decision

Fix **one confirmed user-facing bug**, covering the three JS-0052 occurrences: remove duplicate confirmation ownership. Correct/investigate analyzer configuration before considering mechanical edits. The rest is intentional behavior or optional maintenance/style; the reported severities substantially overstate product risk.

## Confirmed fix: double confirmation on destructive forms

- `Glosify/wwwroot/js/site.js:143-148` handles `submit` for every `form[data-confirm]` at document level.
- `Glosify/wwwroot/js/anki.js:23-27` and `Glosify/wwwroot/js/quiz-details.js:31-33` attach additional submit listeners to those same forms.
- `Glosify/Views/Shared/_AppLayout.cshtml` loads site.js before `RenderSectionAsync("Scripts")`. `Views/Anki/Collection.cshtml:3,67,90,98,100` selects this layout, supplies real removal/delete forms, and loads anki.js. `Views/Quiz/Details.cshtml:5,137,176,215` does the same for quiz-details.js.
- A submit reaches the form listener and then the document listener. Neither checks `event.defaultPrevented`. Accepting produces a second prompt; canceling still produces another prompt, although the eventual submission stays canceled.

**Recommendation:** keep site.js as the single delegated owner and remove the two page-specific confirmation listeners. Consider a defaultPrevented guard in the shared handler if other code cancels submit for validation, supported by a focused regression. Retain confirmation protection; replacing native confirm with custom UI is a separate UX choice, not this bug's fix.

Real Chromium fixture used the exact analyzed scripts with `window.confirm` instrumented to record calls and deterministic answers. The fixture prevents all network submissions. Results:

| Scripts loaded | Mock confirmation answer | Calls for one submit | defaultPrevented before fixture prevents submission |
|---|---|---:|---|
| site.js + anki.js | true for both calls | 2 | false |
| site.js + quiz-details.js | true for both calls | 2 | false |
| site.js + quiz-details.js | false for both calls | 2 | true |

This verifies real DOM event propagation and calls to the confirmation function, not a real deletion against the app. The Anki Cancel case was not separately executed; the identical listener path supports the same conclusion. Fixtures are `anki-confirm-repro.html` and `quiz-details-confirm-repro.html`; results are `.playwright-cli/page-2026-10-11T18-05-31-638Z.yml`, `page-2026-10-11T18-05-49-147Z.yml`, and `page-2026-10-11T18-06-05-953Z.yml`. Browser and loopback server were closed afterward.

## Analyzer/configuration work

**108 alleged syntax errors:** every flagged file parses as an ES module, including the final eight client-test files. Web entry points are explicitly loaded using `type="module"` (Books/Index:160, Books/Read:157, Languages/Index:100, Quiz/Index:314, Home/Index:229, and the assistant layout script); tests and extension packages declare type=module. Existing `eslint.config.mjs:21-22` already uses ecmaVersion=latest and sourceType=module. Do not rewrite imports, exports, or top-level await. Inspect DeepSource's dashboard/module interpretation and flat-config ingestion, then rerun. Parser failures also mean subsequent findings in those files may currently be hidden.

**Six undeclared chrome globals:** all refer to legitimate `chrome.runtime` APIs in extension content scripts. `eslint.config.mjs:26` already declares chrome readonly. [Chrome's content-script documentation](https://developer.chrome.com/docs/extensions/develop/concepts/content-scripts) explicitly includes runtime.getURL, id, onMessage and sendMessage in the directly available APIs. Align analyzer configuration; do not add a fake chrome object.

**Five Worker globals:** helpers in `Glosify.CloudflareTranslator.Worker/src/index.ts` belong to an exported ES module, not the browser global scope. **Three worklet redeclarations:** all point to the `/* global AudioWorkletProcessor, sampleRate, registerProcessor */` annotation at capture.worklet.js:1; there are no runtime declarations to remove. Configure worklet globals correctly without globally assuming all ordinary scripts run in an AudioWorklet.

[DeepSource's analyzer reference](https://docs.deepsource.com/docs/platform/reference/core-analyzers) documents ES-module configuration, explicit globals, and environment settings. Since it currently lists es-modules as the default and this repository already has module-aware ESLint configuration, changing one setting is not guaranteed to resolve the analyzer behavior. Verify effective settings and rerun rather than mass-dismiss JS-0833.

## Rule-by-rule dispositions

| Rule | Count | Decision | Evidence / reason |
|---|---:|---|---|
| JS-0002 | 1 | keep-intentional | The console message is intentional local Playwright review output, not production browser logging. scripts/avatar/browser-review.js:1-4 defines the CLI callback; line40 reports the test result. |
| JS-0052 | 3 | fix-root-cause | Keep a single confirmation owner: site.js:143-148 already delegates all form[data-confirm] submissions. anki.js:23-27 and quiz-details.js:31-33 add duplicate form listeners. Both are loaded after site.js via _AppLayout and page Scripts sections. Real browser fixture proves two confirm calls per submit. Native confirm itself need not be replaced. |
| JS-0066 | 1 | style-only | quiz-settings.js:232-234 intentionally converts nonempty search text to a boolean when deciding item.hidden; !!query is correct and unambiguous here. No defect. |
| JS-0067 | 5 | analyzer-configuration | These helpers are module-scoped in a Cloudflare Worker: src/index.ts exports Env and the default handler. They do not introduce browser globals. Align analyzer module interpretation; retain helpers. |
| JS-0073 | 2 | keep-intentional | No captured-loop bug: tts.js:486 invokes Array.some synchronously before the next iteration; app-settings.js:20 uses const trigger, which has a distinct binding for every iteration. The event callback intentionally updates the outer opener state. |
| JS-0085 | 3 | analyzer-configuration | All three occurrences point to avatar/capture.worklet.js:1, a /* global AudioWorkletProcessor, sampleRate, registerProcessor */ annotation. There are no variable redeclarations. Configure audio-worklet globals/resolve analyzer annotation handling; do not edit working runtime code. |
| JS-0086 | 1 | style-only | anki.js:52 intentionally sets button.disabled=true in a forEach callback. The assignment has the required side effect; its return value is ignored. A block body would be cosmetic. |
| JS-0093 | 1 | keep-intentional | scripts/avatar/browser-review.js:1 explicitly instructs using playwright-cli run-code --filename. The top-level async page=> callback is the required input expression for that runner, not an accidentally unused application expression. |
| JS-0102 | 79 | style-only | The tts.js local var declarations are close to use. Moving 79 declarations to function beginnings does not fix observed behavior and increases diff churn. Prefer any modernization as one optional small TTS refactor with existing tests. |
| JS-0118 | 2 | keep-intentional | tts.js and local-time.js are classic scripts loaded without type=module from _AppLayout. Their IIFE-level use strict directives are valid and intentional. Do not remove strict mode to satisfy a module-style assumption. |
| JS-0119 | 5 | keep-intentional | Deferred initialization is guarded/assigned before use: tts response assigned by awaited fetch or throws; Promise executor sets resolveResult synchronously. settings opener checked through available(), next checked before focus; sidebar touch checked before use. |
| JS-0123 | 1 | style-only | subtitles.js:559 map(message=>message.text) intentionally names each chat message. It does not access or modify the outer runtime message; rename only when touching that block. |
| JS-0125 | 6 | analyzer-configuration | All six are chrome.runtime calls inside extension content scripts. Chrome supplies these documented APIs. eslint.config.mjs already declares chrome readonly; investigate DeepSource ingestion/dashboard config instead of adding bogus declarations or changing runtime behavior. |
| JS-0129 | 6 | keep-intentional | All accesses occur after initialization at execution time: preparationRevision/pendingPreparation are initialized at tts.js:330-331 before API exposure at532; finish closures reference cancel only after cancel assignment at192/247. Source ordering alone is not a runtime use-before-initialization. |
| JS-0239 | 98 | style-only | These 98 tts.js var declarations are working legacy style. A considered const/let pass can improve readability but must preserve function scoping and cancellation behavior; not 98 independently actionable bugs. |
| JS-0240 | 17 | style-only | Object property shorthand in tts.js is optional syntax modernization, with no demonstrated behavior change. |
| JS-0241 | 29 | style-only | Arrow callbacks in tts.js are optional syntax modernization. Current callbacks function correctly; avoid bulk rewrites solely to reduce issue count. |
| JS-0244 | 1 | style-only | tts.js:48-54 safeCall intentionally forwards arguments after callback using arguments/apply. Rest parameters could clarify the signature but current behavior is valid and exercised by tests. |
| JS-0245 | 1 | style-only | tts.js:51 apply correctly forwards optional arguments. A rest/spread rewrite is an optional single cleanup, shared with JS-0244 and JS-W1038. |
| JS-0246 | 9 | style-only | These nine tts.js string concatenations build locale, storage, catalog, and display strings correctly; template literals are preference. |
| JS-0833 | 108 | analyzer-configuration | All 108 flagged files parse successfully as ES modules using node --input-type=module --check (Node26.7). Flags point to import/export or top-level await in module files. Existing eslint.config.mjs already sets sourceType=module/ecmaVersion=latest. Audit DeepSource parser/config ingestion, confirm dashboard module setting, then reanalyze; do not rewrite valid syntax. |
| JS-C1002 | 5 | style-only | Short names are local and clear in context: t is the translation helper in three scripts; a/b are normalized locales inside sameSpeechLanguage. Naming preference, not a defect. |
| JS-R1005 | 59 | defer-maintainability | Complexity is a maintenance signal, not proof of a failure. Reviewed all 59 reported function regions: most implement guarded UI/state transitions, translation parsing, or rendering. Refactor only with a feature need; prioritize tts playSavedQueue/voice catalog if future changes require it, retaining race/cancellation tests. |
| JS-W1038 | 9 | keep-intentional | All nine calls invoke safeCall(callback, ...values). tts.js:48-54 intentionally accepts and forwards additional arguments through arguments/apply; the declared single named parameter is not an arity restriction. Callback arguments are correct. |
| JS-W1044 | 7 | style-only | Optional chaining is optional modernization for these guarded tts values. Preserve input normalization semantics (&& can preserve a falsey primitive, whereas ?. only guards null/undefined); no need for a blanket replacement. |

## Verification and limits

- All **108/108** alleged JS-0833 syntax-error files passed `node --input-type=module --check` against analyzed-master copies (local Node v26.7.0). This validates syntax, not runtime imports or browser behavior. CI targets Node24; that runtime was not substituted for this check.
- **54/54 focused existing tests passed**, 0 skipped: `Glosify.ClientTests/tts.test.js`, `Glosify.ClientTests/quiz-submission-races.test.js`, `Glosify.LiveSubtitles.Extension/test/chat-buffer.test.js`, and `Glosify.Translator.Extension/test/translator-state.test.js`. Output: `javascript-tests.log`. These were run from the immutable analyzed-source snapshot, not the dirty working tree.
- Read all 25 rule groups, all cited syntax lines, and the complete surrounding logic for all 59 complexity occurrences. Static complexity alone was not treated as a bug. No broad production/browser suite or provider call was run.
- The safe loop and deferred-initialization decisions are supported by concrete control flow and the [ECMAScript Array.some algorithm](https://tc39.es/ecma262/multipage/indexed-collections.html#sec-array.prototype.some) and [Promise construction semantics](https://tc39.es/ecma262/multipage/control-abstraction-objects.html#sec-promise-executor).
- [issues.csv](issues.csv) has one row per actual DeepSource occurrence ID, retaining file, line, rule, decision and rationale. Counts may change after the parser issue is resolved; newly visible warnings need review.

## Suggested order

1. Consolidate confirmation handling and add the focused browser/DOM regression.
2. Resolve effective JavaScript module/globals/worklet analysis settings; rerun and review newly visible results.
3. Keep style-only warnings out of the urgent backlog. If TTS is next changed, consider a narrow const/let/rest modernization with existing cancellation and credit-consent tests. Do not spend a standalone campaign on 79 declaration-placement warnings, 98 var warnings, or 59 complexity warnings.

Disposition totals: {"analyzer-configuration": 122, "defer-maintainability": 59, "fix-root-cause": 3, "keep-intentional": 26, "style-only": 249}.
