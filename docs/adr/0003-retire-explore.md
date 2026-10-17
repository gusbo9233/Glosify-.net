# ADR 0003: Retire Explore and public library sharing

- Status: Accepted
- Date: 2026-10-17

## Decision

At the owner's request, remove Explore and its functionality from the active
application. The product will focus on each learner's own library and practice.
This is a feature retirement, not a navigation-only change or a feature flag.

Remove the Explore MVC pages, mobile `/api/explore` catalogue and copy endpoints,
public quiz/collection publishing controls and endpoints, public-read/copy service
methods, and assistant public-library browsing, copying, and visibility actions.
Former HTTP routes return 404. Saved assistant proposals for retired operations
are terminally rejected before applying any changes. Undo of already-applied
sharing journals and historical study-event filtering remain supported. Existing owned quizzes, collections,
practice modes, imports/exports, and Anki remain available.

## Preservation and recovery

The implementation is preserved on the remote branch
[`archive/explore-before-retirement-2026-10-17`](https://github.com/gusbo9233/Glosify-.net/tree/archive/explore-before-retirement-2026-10-17),
at commit `3868cbe16bc153f87394b33d39335a1f1b03cf19`.
This is the committed baseline before retirement; the concurrent Home-page and
flashcard styling edits were not included in that archive.

To restore, inspect that branch and deliberately port the feature and its tests
into a new working branch. Do not reset the active branch to the archive: that
would discard subsequent work. Reassess permissions and API behavior before
restoring any public access.

## Data and compatibility

No destructive database migration is included. Legacy `IsPublic` and original-copy
provenance fields and historical migrations are retained for data preservation;
those flags no longer grant public access. Existing API DTO shapes are retained,
with remaining visibility fields reporting `false`. Old mobile clients lose the
retired browsing/copy/publishing endpoints and must stop offering those actions.
Other API response contracts and ownership checks are preserved.

Previously queued assistant folder placements retain ownership validation and
move approval handling. New quizzes are created directly in their owned folder;
a legacy public flag no longer triggers a publishing workflow.

Historical review records and unused translations may still mention Explore;
they do not make the retired feature available.
