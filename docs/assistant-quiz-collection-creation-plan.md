# Assistant Quiz And Collection Creation Implementation Plan

## Summary

Allow Glosify's assistant to propose new quizzes and collections from the global assistant and quiz library. The assistant must not save these immediately. It should queue `create_quiz` and `create_collection` pending changes, show them in the existing assistant review card, and persist them only after the user clicks Apply.

## Current State

- Quiz-scoped assistant routes already support `/Quiz/{quizId}/Assistant/Send`, history, Apply, and Reject.
- Existing assistant tools can list/edit quiz words and queue word/sentence changes.
- Global assistant routes exist at `/Assistant/Send` and `/Assistant/History`, but currently have no tools and no persisted history.
- Quiz and collection creation already exists through `IQuizService.CreateQuizAsync` and `ICollectionService.CreateCollectionAsync`.
- The assistant frontend already renders generic pending changes, so this feature should reuse that UX.

## Data Model Changes

Update `AssistantThread` so it can represent a global assistant thread:

- Change `QuizId` from `Guid` to `Guid?`.
- Keep `UserId`, `Title`, `CreatedAt`, and `UpdatedAt`.
- In `GlosifyContext`, make the `AssistantThread -> Quiz` relationship optional.
- Keep cascade delete from quiz to quiz-scoped assistant threads.
- Add or update indexes:
  - `{ QuizId, UserId }` for quiz-scoped lookup.
  - `{ UserId, QuizId }` or equivalent for global lookup where `QuizId == null`.

Create an EF migration:

```powershell
dotnet ef migrations add MakeAssistantThreadsSupportGlobalScope --project Glosify\Glosify.csproj
```

## Assistant Contracts

Extend `PendingChangeKinds`:

```csharp
public const string CreateQuiz = "create_quiz";
public const string CreateCollection = "create_collection";
```

Use these pending change payloads:

```json
{
  "kind": "create_quiz",
  "name": "Travel Basics",
  "source_language": "English",
  "target_language": "Spanish",
  "collection_id": null
}
```

```json
{
  "kind": "create_collection",
  "name": "Food",
  "language": "Spanish",
  "parent_collection_id": null
}
```

Update `AgentToolContext`:

- Make `QuizId` and `Quiz` nullable.
- Add `CurrentLanguage`.
- Keep `UserId`, `FocusedWordId`, `FocusedWordLabel`, and `PendingChanges`.
- Quiz-mutating tools must return a clear error when no quiz context exists.

## Tooling Changes

Extend `AssistantTools` with global-safe declarations:

- `list_collections`: returns collections for the current language.
- `list_quizzes`: returns user quizzes, optionally filtered by current language.
- `create_collection`: queues `create_collection`.
- `create_quiz`: queues `create_quiz`.

Validation rules:

- `create_collection` requires `name` and `language`.
- `create_quiz` requires `name`, `source_language`, and `target_language`.
- If current language exists, use it as the default `target_language` / collection `language`.
- Validate `collection_id` and `parent_collection_id` as GUID strings before queueing.
- Do not directly call `IQuizService` or `ICollectionService` from tools; tools only queue changes.

## Orchestrator Changes

Add global orchestration to `IAssistantOrchestrator`:

```csharp
Task<AssistantTurnResponse> SendGlobalMessageAsync(
    string userId,
    string userMessage,
    string? model = null,
    CancellationToken cancellationToken = default);

Task<AssistantHistory> GetGlobalHistoryAsync(
    string userId,
    CancellationToken cancellationToken = default);
```

Implementation details:

- `SendGlobalMessageAsync` gets or creates one global `AssistantThread` where `QuizId == null`.
- It builds a global system instruction with app/library creation rules.
- It passes all global-safe assistant tools.
- It stores history and pending changes the same way quiz-scoped turns do.
- Existing `SendMessageAsync(quizId, ...)` remains quiz-scoped and unchanged behaviorally.

Global system instruction rules:

- The assistant may propose quizzes and collections.
- Mutating creation tools queue changes only.
- If language is missing, ask the user for it.
- If the user asks to add words to a new quiz, explain that v1 creates the quiz first; content can be added after opening it.
- Do not mention tool names, ids, JSON, or internals.

## Apply Changes

Broaden `ChangeApplier` so it can apply global and quiz-scoped changes:

- Update `IChangeApplier.ApplyAsync` to accept `Guid? quizId`.
- For existing word/sentence changes, require `quizId` and preserve current ownership checks.
- For `create_collection`, call `ICollectionService.CreateCollectionAsync`.
- For `create_quiz`, call `IQuizService.CreateQuizAsync`.
- Catch duplicate/invalid collection errors and skip or surface a clear failure.
- Mark the assistant message as Applied only after applying all valid changes.

Return richer apply metadata from the controller if useful:

```json
{
  "applied": 1,
  "createdQuizId": "...",
  "createdCollectionId": "..."
}
```

## Controller Changes

Update global assistant endpoints in `AssistantController`:

- `/Assistant/History` calls `GetGlobalHistoryAsync`.
- `/Assistant/Send` calls `SendGlobalMessageAsync`.
- Add `/Assistant/Apply/{messageId:guid}`.
- Add `/Assistant/Reject/{messageId:guid}`.
- Keep existing quiz-scoped Apply/Reject routes.

All routes must use `User.GetUserId()` and enforce ownership through thread/message ownership checks.

## Frontend Changes

Update `wwwroot/js/assistant.js`:

- `applyUrl(messageId)` should use global routes when `quizId` is null.
- `rejectUrl(messageId)` should use global routes when `quizId` is null.
- Keep existing quiz-scoped URL behavior.
- Render pending changes using the existing review card.
- Change pending-card heading for creation changes to `Review library changes`.
- After applying:
  - If `createdQuizId` exists, navigate to `/Quizzes/Details/{createdQuizId}` or reload if route generation is not available.
  - Otherwise reload the current page.

No new modal is required.

## Summary Rendering

Update `AssistantOrchestrator.BuildSummary`:

- `create_quiz`: `Create quiz "Name" (Source -> Target)`.
- `create_collection`: `Create collection "Name" in Language`.
- Keep existing word/sentence summaries unchanged.

## Tests

Add service tests for:

- `create_collection` pending change creates a collection owned by the user.
- `create_quiz` pending change creates a quiz owned by the user.
- Invalid parent collection id does not create a collection.
- Mismatched quiz collection language does not create a quiz.
- Word/sentence pending changes still require quiz context.
- Applied and rejected messages cannot be applied again.

Add controller tests for:

- Anonymous `/Assistant/Send` is rejected/redirected according to existing auth behavior.
- Global assistant history returns persisted global messages.
- Global Apply and Reject enforce message ownership.
- Existing quiz assistant Apply still works.

Add frontend/manual checks:

- Open global assistant from quiz library.
- Ask: `Create a Spanish travel collection`.
- Confirm review card appears.
- Reject it and confirm nothing is created.
- Ask again, Apply, and confirm the collection appears.
- Ask: `Create a quiz called Travel Basics from English to Spanish`.
- Apply and confirm the quiz appears or navigates correctly.

## Implementation Order

1. Update assistant thread schema for nullable quiz scope and add the migration.
2. Extend pending change kinds, payload parsing, and summary rendering.
3. Broaden `AgentToolContext` and guard existing quiz-only tools.
4. Add global-safe list/create assistant tools.
5. Add global orchestrator methods and persisted global history.
6. Add global Apply/Reject endpoints.
7. Update `assistant.js` to use global Apply/Reject routes when no quiz is selected.
8. Add service/controller tests.
9. Run the test suite and perform the manual assistant flow checks.

## Assumptions And Defaults

- Creation is available from the global assistant and quiz library.
- Assistant-created quizzes and collections are queued and user-approved before saving.
- Existing quiz and collection services remain the source of truth for validation and persistence.
- The assistant should not create words inside a newly created quiz in the same Apply operation in v1.
- If the current language is unavailable, the assistant asks for a target language instead of guessing.
