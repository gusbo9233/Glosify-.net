# Anki requests in assistant chat

The language assistant can work with Glosify's built-in Anki study collections.
They are separate from the collections that organize quizzes in the library.
External Anki export/synchronization, deletion, renaming, and study-setting changes
are not exposed by these tools.

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
same transaction. No new HTTP endpoints or migrations are required.

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
