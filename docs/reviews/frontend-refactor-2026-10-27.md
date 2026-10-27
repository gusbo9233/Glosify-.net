# Frontend refactor: implementation and verification

The web frontend now has feature-owned TypeScript modules, a generated asset
pipeline, and tests that execute compiled production code. MVC remains responsible
for page markup and HTTP rendering. See the
[frontend guide](../../Glosify/Frontend/README.md) for ownership and commands.

## Implemented

- Maintained scripts and styles live in `Glosify/Frontend`, grouped by feature.
  Generated `wwwroot/js` and `wwwroot/css` assets are ignored by Git.
- An explicit entrypoint manifest preserves existing browser URLs, script formats
  and initialization order. Esbuild generates assets before ASP.NET static-asset
  discovery, with production minification, shared chunks, separate avatar bundles
  and preserved upstream license notices.
- All maintained browser entrypoints and modules are strict TypeScript, including
  the assistant, PDF reader, speech settings/playback, quiz and Anki controllers,
  guest behavior, shared services, avatar renderer and conversation controller.
  Capture and decoder workers have a separate environment configuration.
- Assistant shell, transcript, run controls, transport, feedback and message parts
  have explicit module boundaries. Reader rendering, cancellation, selection,
  highlights, segmentation and speech are separate modules. Speech preferences,
  voice catalogs, chunking and playback have separate ownership.
- Smaller feature modules separate quiz word selection, practice ranges, typing
  keyboards, JSON import state/tree rendering, localization, modals and learning
  refresh coordination. Existing security checks, event contracts, storage keys,
  API responses and global service bridges are preserved.
- Maintained code tests load complete compiled modules or classic-script bundles.
  Source slicing and import stripping were removed from these tests. The avatar
  lifecycle harness compiles the complete controller as CommonJS for explicit
  dependency replacement; it still inspects internal lifecycle state. The existing
  vendor-specific PDF.js cancellation source probe is retained.
- The large shared stylesheet is split into ordered feature sections. The original
  cascade is preserved, apart from a verified reader toolbar overlap fix: desktop
  translation controls occupy a separate row, and mobile speech settings retain
  their icon and accessible name without the wide text label. Fixed icon boxes
  prevent unavailable web fonts from expanding the toolbar over the Back button.
- CI checks frontend lint/types and published output. Publish validation checks
  configuration, vendor assets, sample PDFs, generated entrypoints and exclusion
  of frontend sources, dependencies and production source maps.
- The assistant's existing `pending` and `remove_circle` icons were missing from
  the font subset. Both are included, and the inventory test scans typed icon maps.

The October analyzer findings were checked against current source. Its confirmed
web fixes—accessible labels, effective hover styling and PDF thumbnail cleanup—
were already present and are preserved. Historical reports were not rewritten.
The existing feature-sized Razor views remain intact; splitting them solely by
line count would add indirection without moving state or behavior ownership.

## Verification

- 435 client tests passed, including feedback recovery, speech consent/antiforgery,
  delegated pronunciation, localization/return paths and reader speech chunking.
- Full .NET suite: 1,895 passed, 60 skipped, zero failures.
- Frontend lint, both TypeScript configurations, client/extension/tooling lint and
  the Release build passed. The avatar preview build passed during the initial
  build-pipeline stage.
- Release publish and `--no-build` publish were checked. A clean generated-assets
  build was exercised during the initial stage. The validator rejects an
  intentionally incomplete package missing application configuration.
- Twelve generated stylesheets still match the original after equivalent
  minification. The shared stylesheet differs for the documented toolbar fix;
  all thirteen matched before that fix. Classic entries retain classic output.
- Earlier published runs in this continuation passed 11 feature journeys and
  three assistant journeys (two overlapping), covering guest browsing/reader,
  quiz/Anki practice, imports, recovery, assistant chat/apply, focus and login.
  Final published verification covered 15 distinct journeys: 13 passed in the
  combined run; the responsive reader check exposed missing-font icon expansion,
  which was fixed, and the no-JavaScript login journey timed out waiting for load.
  Both passed on the next run, together with the reader selection/speech journey.
  Real Rain/Rowan loading and decoder execution passed in the combined run.
  The login timeout is recorded as an intermittent test failure, not erased by
  the passing rerun.
- The new reader interaction test reproduced blocked Read-button clicks against
  the original published build at the default desktop width. The translation
  language selector overlapped the button. The responsive regression exercises
  real pointer reachability at 390, 768, 1280 and 1920 pixels in LTR and RTL.
- The earlier selected Swedish-language journey reported an aborted favicon
  request, also reproduced against the original published build. It remains a
  recorded failure, not a clean full-browser-suite result.
- Before/after mobile Anki and flashcard screenshots were byte-identical during
  the initial stage. The desktop Anki difference was the generated test account
  name. The real avatar screenshot was inspected after successful model switching.

Checks used local Node 26.7 and .NET 10; CI is configured for Node 24, which was
not installed locally. Browser checks use local Chrome and the isolated
`glosify-frontend-browser-20261027` database. Production was not deployed.

## Limits

Live paid-provider calls and physical microphone playback were not tested. The
skipped .NET cases, full browser suite, exhaustive mobile/RTL visual coverage and
remote CI were not verified. Local review was performed; Greptile has not reviewed
these unpushed changes. Vendor libraries and the separate browser extensions were
not migrated. Existing global service bridges remain compatibility contracts.
