# Anki requests in assistant chat

The language assistant can work with Glosify's built-in Anki study collections.
They are separate from the collections that organize quizzes in the library.
External Anki export/synchronization, whole-collection deletion and study-setting
changes are not exposed by these tools.

Examples:

- “Add the first 20 words of this quiz to my Revision Anki collection.”
- “Create an Anki collection for these words, with cards in both directions.”
- “Link this quiz so future words and sentences appear in my collection.”
- “Which Anki collections have cards due?”

“First” means quiz-page creation order, with an ID tie-breaker. The existing
alphabetical default of `list_items` remains compatible; the assistant chooses
`order: created` for ordered selections. It pages through content and sends exact
IDs, up to 100 per addition. If fewer items exist, the assistant reports the
available count. Missing items fail the batch rather than substituting others.

Existing collections supply the default direction. New collections inherit the
source quiz's language pair and default to translation → learning language and
UTC. Explicit “both directions” creates two cards per item. Existing active cards
are counted as already included without resetting their scheduling. A card that
was only quiz-linked gains direct inclusion, so it remains if the quiz is later
unlinked; Undo restores its previous membership.

## Tools and persistence

`list_anki_collections` and `get_anki_collection` provide read-only, owned,
paginated views. They do not invoke the synchronizing MVC read paths.
`create_anki_collection`, `add_anki_items`, and `link_anki_quiz` return proposed
changes; the existing runtime applies them immediately and journals them in the
same transaction. No new HTTP endpoints are required. Learning history adds the
`TrackQuizAttemptItems` migration described below.

Selected items are directly included; adding a selection never creates a quiz
link. Whole-quiz linking is additive: existing directions, exclusions and review
history remain intact, and future quiz content synchronizes through existing
Anki services. Saved tool outputs include collection identity and URL, selected
item count, added card count, already-included count, and preserved exclusions.

Undo restores membership without resetting study progress. It compares the
saved semantic state with current state, preserves reviewed/edited cards, and
reports conflicts as kept changes. A whole-quiz link is kept if its affected
cards or link changed later, including new synchronized cards. A newly created
collection is removed only when unchanged and empty after reversing its content
changes. EF rowversion tokens still enforce write concurrency, but are not part
of the semantic Undo fingerprint: reversing a later change in the same run can
advance a token without changing the meaning of an earlier journal entry.

## Verification

`AssistantAnkiTests` exercises discovery, exact first-20 selection, both
directions, sentences, duplicates, ownership, language compatibility, stale
selection, rollback, restarts, read-only pagination, and conflict-aware Undo.
The opt-in SQL Server test covers chained creation/addition/link Undo with real
rowversion generation.

The browser journey `AssistantAnki_SelectedQuizItemsAppearInCollectionAndUndoRemovesThem`
scripts model completion, writes through the real change applier, checks the
collection page and uses the production Undo endpoint. It requires the normal
isolated browser host/database configuration described in README. It does not
claim to test a live model's interpretation of the request.

Run the normal .NET suite with `dotnet test Glosify.slnx`. SQL Server and browser
journeys report skips when their explicit test prerequisites are absent.

## Learning assistance and maintenance

Additional requests supported by the language assistant:

- “Add the 20 words I struggled with most this month to Revision.”
- “How did my quiz practice go, and what Anki reviews are coming up?”
- “Make a quiz from my mistakes, with example sentences.”
- “Rename Revision to Travel; remove these cards; stop syncing this quiz.”
- “I have ten minutes. Give me something to practice.”
- “Make a quiz from the first page of this book/transcript and add it to Anki.”

`get_learning_mistakes` ranks existing owned quiz words/sentences in the selected
language by mistakes (quiz incorrect answers plus Anki Again ratings), then mistake
rate, recency and stable IDs. It returns current text, translations, IDs and language
pairs. Defaults are 30 days and 20 items; windows are bounded to 365 days and pages
to 100 items. Deleted items and unidentifiable legacy results are not selected.
`create_quiz` and `add_items` provide focused quizzes and requested example
sentences; selected Anki additions use the existing items directly.

`get_learning_progress` reports quiz accuracy excluding skips and Anki retention
(non-Again reviews) separately with observation counts and a time window. Empty
history has null rates. An optional collection adds its last-30-day statistics
and 14-day due forecast in the collection timezone. The current due count includes
overdue cards; the forecast shows scheduled dates, not future rescheduling.
All assistant statistics reads are observational and do not synchronize cards.

`prepare_study_session` provides an owned local study link. Automatic selection
prefers due Anki cards, then new cards, then the current quiz (or an owned quiz in
the selected language). The time budget is an estimate at two items per minute,
not a timer. Anki respects the normal daily limits. Typing and flashcard links
can select exact words; sentence subsets require a focused quiz first.

`rename_anki_collection` saves with Undo. `remove_anki_cards` and
`unlink_anki_quiz` use the chat's approval policy and provide readable summaries.
Removal excludes linked cards from future synchronization. Unlinking leaves
directly included cards active. Both retain card scheduling and review history.
Changes are checked against the proposed state at apply time; Undo also preserves
later edits and reviews. Source deletion prevents restoring an active removed card.

The additive `TrackQuizAttemptItems` migration stores nullable historical item
IDs and skip flags. It does not infer or backfill IDs from old prompt text. New
typing attempts record IDs; new flashcard attempts record per-card ratings and
IDs. Legacy aggregate-only attempts still contribute to overall statistics and
are counted separately as unavailable for item-level selection. Apply the
migration through the project's normal deployment process before running this code.

`AssistantLearningTests` covers recorded mistakes, accuracy/retention, ownership,
read-only study links, approval/rejection, stale changes, history-preserving Undo,
and complete book/transcript → quiz → Anki workflows with scripted model calls.
`QuizAttemptServiceTests` covers ID capture and remembered/Again/skip recording.
Scripted tests validate application behavior, not live model selection quality.
