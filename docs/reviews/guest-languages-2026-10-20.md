# Guest examples follow the selected language

The logged-out language pill respected the user's Polish cookie, but the sample
view models, practice payload, speech labels, links, and book PDF were fixed to
Spanish. GuestSamples now builds an immutable sample set from the same language
context used by the selector. Guest cookies retain precedence over legacy account
cookies. No selection defaults to Spanish. Language-independent `/sample` URLs
preserve picker return destinations; old `/spanish` URLs remain compatible.

All 69 learning languages have five starter phrases, a short greeting dialogue,
a saved translation, a quiz/Anki practice set, and a one-page bilingual PDF.
English uses Spanish meanings. Freestyle has a small solar-system example.
Content is bundled; examples never query personal resources or paid providers.

## Validation

- 122 focused .NET tests passed: GuestBrowsingTests, SelectedLanguageFilterTests,
  and DemoAccountOptionsTests. Catalog-wide cases cover every language plus
  Freestyle. Specific regressions cover the original Polish cookie, guest-cookie
  precedence, picker return navigation, source/translation streams and old links.
- All 395 client tests passed, including Polish composed/decomposed Unicode,
  Turkish casing and reverse English-learning practice with Spanish answers.
- Local Chrome journey passed: Polish library and quiz detail, typing two correct
  answers, reverse flashcards with Polish speech metadata, completing all five
  Anki cards, both transcript streams, actual PDF reader text, and switching to
  Japanese while returning to quiz settings. No browser page errors occurred.
- PDF.js loaded and rendered all 70 generated PDFs; every document has one page
  and extractable text. Contact sheets were visually inspected for legible glyphs,
  clipping, layout and right-to-left scripts. Screenshots are in the ignored
  `output/playwright/guest-languages/` directory.
- `git diff --check` passed. Unrelated working-tree edits were preserved.
- Full database-dependent .NET suite and hosted CI were not run for this change.

## Review

The Qodo standards search returned no applicable rules. Initial auto-depth review
`494e1b23-555c-465f-bfb3-e809de7ceae1` completed against the older master merge
base `ac98a8586109e77a7f394f4974072e6d403fde6e`. Its selected-path snapshot omitted
already-committed branch dependencies and produced six findings, assessed locally:

- `466f082a`: rejected. `IAnkiCollectionService.GetWorkspaceAsync` and the matching
  Anki models exist in current HEAD; the actual project builds and tests pass.
- `890eac00`: rejected. `StudyCardViewModel` already declares `PromptIsTarget`,
  `Label`, `ShowRatingShortcuts`; `StudyRating` declares `ShortcutKey`.
- `3dc17e4d`: rejected. Current `anki.js` handles membership changes and constructs
  the `Add`/`Remove` inputs (lines 63–87).
- `ec319e72`: rejected. `Program.cs:62` registers `SelectedLanguageFilter`.
- `e41513e3`: rejected for the local implementation. All 70 PDF assets exist and
  passed HTTP and rendering checks. They were deliberately excluded from textual
  review. They must accompany the code when this work is committed/published.
- `da72ed1d`: rejected. `site.css:783` expands the reveal button with absolute
  positioning and `inset: 0`; this CSS exists in current HEAD.

Because the first review's snapshot was incomplete, it is not clean coverage of
the current implementation. A replacement auto-depth review was submitted as
`c2b5da33-16d0-4400-a1b4-baba93dc8a06` against published HEAD
`929674704282383694b6868a5bee882af1e34caf`, verified with an exact remote fetch.
Binary PDF assets remain excluded from text review and are validated locally.

DeepSource has no new commit to analyze because this change remains local.
Copilot/Greptile: credits exhausted; temporary policy. No PR or merge requested.

The replacement review completed with two new findings:

- `a16a9094`: accepted and fixed. A no-cookie guest book resolved Spanish content
  but emitted an empty reading language. The reader now uses the resolved sample
  for both PDF URL and reading language. Tests assert the default Spanish value
  and each catalog entry, including English for Freestyle. All 122 focused server
  tests passed again. The restarted localhost response was checked directly:
  `starter-es.pdf` and `data-reading-language="Spanish"`.
- `6a0dc58f`: rejected after browser reproduction. Empty selected-word filters
  render `Complete · 0 of 0 correct` in typing and flashcards; restart also works.
  No page errors occur. `createPractice.done` is true for zero cards, and each
  `state.current` access in rendering is protected by that completion state.

The replacement review also carried historical findings from other reviews.
The six synthetic snapshot issues are assessed above. Earlier guest concerns
about cookie isolation, picker returns, reverse speech, restart and saved speech
settings are covered by the current server/client/browser checks. Historical
Home, sharing, folder-undo and authenticated Anki-sync findings are outside this
local multilingual change; they are not treated as new findings or marked fixed.

Follow-up auto-depth review of the verified reader fallback fix was submitted as
`15df854c-dffb-4107-8dcc-045322b3f07c` against the same published HEAD.

Review `15df854c-dffb-4107-8dcc-045322b3f07c` completed, marked the reader fallback
finding resolved, and raised `87924a1f` about Freestyle labels. Accepted and fixed:
browser practice now uses Question/Answer labels and instructions for the
solar-system sample instead of English-to-English translation wording. Real
Chrome checks passed for typing (Mercury scores correctly), flashcards and
reverse typing; all 395 client tests passed again. No server code changed in this
last correction.

Final auto-depth review `c8226b11-2b52-4eb3-a6b4-f3ff06ce2f38` completed with
complete coverage of the submitted textual scope. It introduced one finding,
`7768025f`, claiming that the guest login click handler blocks logout. Rejected:
the actual selector is `.app-navigation-auth[href]`, while the logout control is
a submit button without `href`. An isolated browser form using the exact logout
markup, with the real guest-login module loaded, submitted normally and did not
open the login dialog. No code change was appropriate. The final review's
historical finding state is not a clean whole-repository bill of health; the
scope and earlier dispositions above remain applicable. No actionable finding
from the final pass remains for this change.
