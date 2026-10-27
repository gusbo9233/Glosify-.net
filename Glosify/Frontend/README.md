# Web frontend

Maintained browser code lives here. `wwwroot/js` and `wwwroot/css` are generated
and ignored by Git. Vendor code under `wwwroot/lib` and model/image assets are
unchanged. Keep MVC views responsible for markup and page entrypoint selection.

Use Node 24 and install the locked dependencies before a clean .NET build:

```sh
npm ci --prefix Glosify/Frontend
npm run lint --prefix Glosify/Frontend
npm run typecheck --prefix Glosify/Frontend
npm test --prefix Glosify.ClientTests
dotnet build Glosify.slnx
```

The web project's build target generates assets before static asset discovery.
Release builds minify; Debug builds retain readable output and local source maps.
Production output excludes source maps and all frontend sources/dependencies.
`dotnet publish --no-build` requires a preceding build of the same configuration.
Do not run Debug and Release builds concurrently: both generate the webroot.

For editing, run `npm run watch --prefix Glosify/Frontend` with the local app.
Restart/rebuild the ASP.NET host when entrypoints or shared chunk names change,
so its static asset manifest can discover them. Existing pages should be reloaded
after rebuilding. This is asset watching, not hot module replacement.

## Ownership

Feature folders own their DOM behavior, state, and feature helpers. `shared`
contains the existing app-wide services. Prefer explicit module exports and
imports; create shared helpers only when there are real shared consumers.
Retain established event names and storage keys during internal refactors.

`entries.json` maps existing browser URLs to maintained sources. Add new
entrypoints there and link them in the owning Razor page with `asp-append-version`.
Each entry declares its output format explicitly. Existing classic scripts retain
IIFE output and their current initialization order, while their source can import
feature modules. New entrypoints should use ES modules and matching Razor tags.

The avatar scene and decoder are separate bundles. The decoder's existing URL
is intentionally stable because the server grants only that worker WASM
execution. Keep its CSP and the worklet lifecycle intact. Upstream notices are
copied alongside the generated bundles. `scripts/avatar` still owns model
creation and preview tooling; its runtime build delegates to this pipeline.

## CSS

`styles/site.css` is an ordered import list. Its sections preserve the original
cascade, including final responsive and brand overrides. The build resolves
these imports into one stylesheet. Other styles retain their page-specific
Razor links. Do not reorder selectors or remove apparent duplicates without
checking affected pages at desktop/mobile widths and in RTL.

## Modules and testing

All maintained web entrypoints, feature modules, avatar rendering code and audio
workers are strict TypeScript. Workers have a separate TypeScript configuration
so they cannot accidentally depend on DOM globals. Build/preview tooling remains
JavaScript; vendor libraries and the separate browser extensions are outside this
migration. Do not bypass type checking with `any` or `@ts-nocheck`.

- Assistant modules own transport/run commands, context state, shell focus,
  transcript rendering, run controls, message parts and feedback. The page
  controller coordinates chat selection, context writes and committed changes.
- Reader modules own PDF loading/render cancellation, segmentation, DOM selection,
  highlight geometry and speech. The page controller coordinates navigation and
  translation requests.
- Speech modules own preferences, voice catalogs, chunking, locale matching and
  playback sessions. The settings controller retains the public `GlosifyTts`
  bridge used by existing pages, including account-scoped billing consent.
- Quiz modules own practice ranges, word selection, typing keyboards and JSON
  import state/tree rendering. Shared modules own localization, modal behavior
  and learning-change refresh coordination.
- Avatar modules own rendering, audio capture/playback, connection recovery,
  conversation memory, retention and floating chat. The typed conversation
  controller coordinates these lifecycles and the existing server protocol.

The test command compiles sources to ignored `Glosify.ClientTests/build` and runs
Node's test runner. Tests import complete compiled modules or execute complete
classic-script bundles with controlled browser dependencies. They do not slice
or rewrite maintained production source. The avatar conversation lifecycle harness
uses an unbundled CommonJS test compilation with explicit dependency replacements;
it still inspects internal lifecycle state. A vendor-specific PDF.js cancellation
regression retains its existing source probe against the vendored library.

Module tests use Node's `--experimental-vm-modules` flag. The test command sets it;
it is not needed by the web application or asset build. Preserve concurrency,
selection, cancellation, accessibility and billing assertions when changing tests.
.NET delivery tests fetch generated entrypoints and their imported chunks and
verify that frontend sources stay private. Browser journeys run against published
output to verify the complete asset graph and DOM behavior.

Existing global service bridges, DOM events and storage keys are compatibility
contracts. Keep implementation state inside feature modules; change those public
contracts only with coordinated caller updates. MVC views remain the markup
owners; this refactor does not introduce a client rendering framework.

October analyzer reports are historical evidence. Confirm current findings
before changing code; preserve the existing accessibility and PDF cleanup fixes.
