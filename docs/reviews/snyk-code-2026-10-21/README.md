# Snyk Code investigation — 2026-10-21

Reviewed all **266 findings** from the successful Glosify Snyk Code scan: **2 high, 7 medium, 257 low**. No exploitable high or medium issue was established in the reviewed flows. Four low-severity findings identify unnecessary personal-data logging; their operational impact depends on the configured addresses, log access, and retention.

| Disposition | Findings |
| --- | ---: |
| Test fixtures, synthetic inputs, or test-only controllers/listeners | 226 |
| Reported vulnerability not substantiated at the current trust boundary | 36 |
| Confirmed personal-data logging concern | 4 |
| Total | 266 |

Every scanner identity appears once in [findings.csv](findings.csv) and [findings.json](findings.json), with rule, original severity, source location, disposition, and reasoning. The inventory preserves scanner severity; disposition is an independent assessment. Test-call findings that duplicate production logging behavior are explicitly identified. These categories are not scanner suppressions.

## Scope and reproducibility

- Source revision: `bd38d8182767fece37f20976fa35a736702376ea`; working tree was clean during code investigation.
- Scanner: Snyk CLI 1.1308.1, `snyk code test --org=gusbo9233 --json-file-output=/private/tmp/glosify-snyk-code-enabled.json`.
- Scan completed with exit code 1 (findings), zero ignored findings. Raw SARIF remains at the above temporary path.
- This report concerns source analysis. The earlier dependency investigation is separate, at `/private/tmp/glosify-snyk-investigation.md`.
- Reviewed cited code, relevant dataflows/callers, authorization and validation boundaries, rendered partials, test fixtures, and developer scripts. Application code has not been changed as part of this investigation.

## Confirmed logging concerns: four low findings

1. `Glosify/Controllers/DemoController.cs:59` logs the configured demo email and seeding error.
2. `Glosify/Services/Auth/DemoAccountSeeder.cs:91` logs the configured email on creation.
3. `Glosify/Services/Auth/DemoAccountSeeder.cs:105` logs the email and joined Identity error descriptions on failure.
4. `Glosify/Services/Auth/ExternalAccountService.cs:144` logs `Describe(result)`. The helper at line 150 joins Identity error codes **and descriptions**, which can embed an email address. Other calls to this helper share the same concern.

A temporary .NET probe loaded the built application assembly and passed a real `IdentityErrorDescriber.DuplicateEmail` result through the actual private `Describe` helper. The resulting logging argument retained the synthetic email. This confirms the data path; it does not demonstrate public access to logs. Six test-only privacy findings lead to the same production helper, with synthetic data and `NullLogger`; they are not six additional production leaks.

Recommended remediation: remove email fields from demo lifecycle logs and log Identity **codes only**, preserving useful failure categories. Sanitize the seeder's error before the controller logs it as well; removing only the email placeholder leaves the description route. Add focused assertions that emitted error logs do not contain the supplied email and still contain the error code. Review log access/retention separately. The shared demo address may be intentionally public, so its presence is a data-minimization concern rather than proof of sensitive-account disclosure.

## Two high findings

**Hardcoded secret, `Glosify.LiveSubtitles.Extension/lib/tab-auth.js:3`: false positive.** `glosifyPendingTabAuth` is a storage key name, not an authentication credential. Actual authorization state is consumed once and checked against tab/frame, callback origin/path, state, expiry, and PKCE. The relevant tab-auth tests passed.

**XSS, `scripts/avatar/preview/serve.mjs:43`: not substantiated.** The server maps fixed routes to fixed local files and responds with their bytes. It does not reflect the request path into HTML. It binds loopback and validates Host. A live probe returned the allowlisted PNG with the correct MIME type, returned constant 404 responses for script-shaped and traversal paths, and rejected a foreign Host with 403. The associated medium HTTP finding describes this same local preview server.

## Seven medium findings and related low findings

**DOM XSS (3):** `quiz-json-import.js:339` appends a textarea node with a text value, not HTML. `guest-login.js:21` inserts a same-origin MVC login partial whose fields and errors use Razor encoding. `flashcard-quiz.js:76` inserts same-origin study partials; `_StudySession`, `_FlashcardSession`, and shared `_StudyCard` render prompts, answers, and examples with Razor encoding. No raw untrusted HTML path was found. This assessment depends on preserving those response contracts. DOM node insertion is distinct from interpreting strings as HTML; see [MDN append](https://developer.mozilla.org/en-US/docs/Web/API/Element/append).

**Open redirect (2):** `quiz-json-import.js:319` consumes a redirect generated by `Url.Action` for fixed Quiz routes. `flashcard-quiz.js:71` follows the URL of responses from the reviewed study actions, which use fixed controller redirects. No caller-controlled destination was established. A client-side origin guard could add defense in depth but is not evidence of an existing open redirect.

**XML parser (1):** `tools/localization/translate_ui.py:90` parses the fixed, tracked localization `.resx`, not XML returned by an external translation service. The inspected runtime is Python 3.14.7 with Expat 2.7.4. The tracked document parsed successfully; an external-entity probe failed with undefined-entity `ParseError`. ElementTree does not expand external entities, while other XML denial-of-service risks depend on Expat and input trust; see [Python XML security documentation](https://docs.python.org/3.12/library/xml.html). This is not a blanket assertion that every Python runtime or XML input is safe.

**Local HTTP (1):** the preview server described above intentionally serves loopback assets. The two additional `/test` HTTP findings also bind test servers to loopback.

## Antiforgery findings: 13 application locations, one test location

Some scanner locations are class declarations even when the bypass applies to one action. They do not demonstrate a blanket controller exemption. Global MVC antiforgery validation is configured in `Program.cs`.

| Flow | Boundary supporting disposition |
| --- | --- |
| API base controller | Explicit bearer-only authentication; browser cookies do not authorize these actions. |
| Extension authorization exchange | One-time code, redirect URI binding, and PKCE verifier. |
| External authorization | Protected external authentication properties and PKCE; one-time exchange and account eligibility checks. |
| Paid-service status | Read-only GET. |
| Realtime translation relay | One-time session-bound WebSocket relay credential. |
| Stripe webhook, two locations | Signature validation before mutation; checkout POST retains antiforgery. |
| Game usage ingest, two locations | Server-only credential header, configured minimum length, fixed-time comparison. |
| Game data, two locations | SaveProfile requires trusted server credential; ordinary browser writes remain protected. |
| Avatar voice connection, two locations | Exact WebSocket Origin authority and session ownership; ordinary POST mutations retain antiforgery. |
| ContractProbeController test | Registered in the test factory, not the deployed application. |

The underlying distinction is ambient browser credentials versus explicit credentials and protocol-specific validation, not that anonymous endpoints are automatically safe. See [ASP.NET Core 10 antiforgery guidance](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).

## Log forging: six findings

The reported source location alone is misleading; inspected the actual sinks in the SARIF flows:

- Two book deletion flows log a parsed `Guid` document ID; deletion also checks ownership.
- The translation relay logs a `Guid` session ID.
- TTS logs a blob name constructed from a validated voice identifier and SHA256 text digest, not raw text.
- Payment verification logs currency from an authenticated Stripe API response, not the raw submitted session identifier.
- Dispute handling logs an allowed fixed event type after Stripe signature verification; the supplied signature is validation input, not the logged value.

These paths do not establish arbitrary attacker-controlled log lines. This conclusion does not certify every exception message or every unrelated log in the application.

## Other script and extension findings

**Seven Python path findings:** avatar build/preparation paths are operator CLI choices or repository-controlled pinned manifest entries; neither inspected manifest contains an absolute path or `..` entry. Globe generation reads the explicit CLI source file. Localization cache root comes from operator environment, with controlled locale/batch suffixes. No remote input crossing a filesystem boundary was identified. Reassess if these tools become services or consume untrusted manifests.

**Production postMessage listener:** `Glosify.Translator.Extension/overlay/translator.js:68` requires `event.source === parent` and only accepts `host:focus`, which expands/focuses UI. The hosting parent is intentionally untrusted; this route grants no translation, token access, or paid operation. Privileged actions use extension runtime messaging. Outgoing layout messages contain no secrets. This disposition is specific to the limited current handler, not a general endorsement of missing origin validation.

## Test findings: 226

- 208 C# credential findings: dummy identities/passwords/API keys, identifiers, or configuration key names. All cited locations were inspected. Browser account fixtures require the verified loopback test-host/database handshake; the test project is not publishable.
- 6 JavaScript credential findings: mocked access/refresh/API credentials.
- 6 privacy findings: synthetic inputs and `NullLogger`, duplicating the production helper concern above.
- 2 HTTP findings: local test listeners.
- 2 wildcard postMessage findings: intentionally hostile test inputs used to assert rejection.
- 1 postMessage listener finding: test observation capture.
- 1 antiforgery finding: test-only contract probe controller.

Fixture classification does not mean production credentials should be committed to test files. No live credential was established in these cited literals.

## Verification and limits

**273 .NET tests passed; zero failed or skipped.** Filtered suites covered API authentication, extension/mobile authorization codes, external sign-in, avatar access, game integration/usage validation, guest browsing, quiz imports, Stripe payments, and external accounts. TRX: `/private/tmp/glosify-snyk-tests/snyk-triage.trx`.

**39 JavaScript tests passed; zero failed or skipped.** Ran `quiz-json-import.test.js`, `flashcard-refresh.test.js`, `tab-auth.test.js`, `overlay-lifecycle.test.js`, and `worker-behavior.test.js`. Log: `/private/tmp/glosify-snyk-js-tests.log`. Some extension checks inspect source structure; these are not a substitute for hostile-page browser testing.

Additional probes: live loopback preview-server requests; actual Identity error formatting through the built application's helper; XML parsing/entity rejection; pinned-manifest path checks. Temporary Identity probe: `/private/tmp/glosify-snyk-privacy-probe/`.

No production exploit attempts, production log access inspection, full browser suite, or full .NET suite were performed. No scanner exclusions or ignores were added. No application fixes, commits, or deployments were made. Findings remain visible in Snyk until code changes or explicit triage there. This investigation assesses the reported paths at the stated revision, not the absence of all vulnerabilities.
