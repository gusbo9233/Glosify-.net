# Valid code-analysis findings (resolved)

Reviewed on 2026-08-20 against the current source, request flows, tests, configuration, and relevant .NET 10 behavior.

Resolution status: all behavioral, test-reliability, and cleanup findings listed below were fixed in the current working tree on 2026-08-20.

The reviewed exports contain 3,291 JetBrains findings, 3 ESLint findings, 1,989 unique Meziantou findings, and 208 unique Sonar findings. The Meziantou and Sonar logs print each warning twice, so their duplicate build-summary entries were counted once.

A finding is included below only when the current repository demonstrates a behavioral/reliability problem or objectively unused code/API surface. Pure style preferences, intentional compatibility surface, framework/reflection usage, and analyzer parser failures are excluded. No high-severity security vulnerability was validated.

Priority labels:

- **Medium**: can change persisted state, deny access to valid user data, or produce materially wrong feature behavior.
- **Low**: narrower correctness, validation, telemetry, or test-reliability issue.
- **Cleanup**: true unused/redundant code with no demonstrated current behavior impact.

## Behavioral findings

### `Glosify/Controllers/AnkiController.cs`

- **Low — line 158 — Sonar S6967:** `CreateFromQuiz` does not check `ModelState.IsValid`. This is a regular MVC action, so invalid model state does not produce an automatic response. The service revalidates some rules, but not the declared form contract: for example, an overlong or invalid `TimeZoneId` can be silently normalized to UTC while the collection is still created. Check model state before calling the service and add a malformed-form test. ASP.NET Core documents that controller-and-view applications are responsible for inspecting model state: [MVC model validation](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/validation?view=aspnetcore-10.0).

### `Glosify/Models/Api/AssistantApiModels.cs`

- **Low — line 27 — Sonar S6964:** `AssistantClientMetricsInput.ClientDurationMs` is vulnerable to under-posting. The repository does not enable `RespectRequiredConstructorParameters`; therefore `{}` binds the non-nullable constructor value as `0`, which passes `[Range(0, 900000)]`. Both assistant controllers persist that fabricated zero as the turn duration. Make presence mandatory through `required`/`JsonRequired`, a nullable validated value, or the global serializer option, and test `{}`. See [.NET required properties](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/required-properties).

### `Glosify/Models/Api/QuizApiModels.cs`

- **Medium — line 40 — Sonar S6964:** `SetVisibilityRequest(bool IsPublic)` is vulnerable to under-posting. `{}` binds `IsPublic` as `false`, and both the quiz and collection visibility endpoints persist it. A malformed request can therefore silently unpublish an owned quiz or collection instead of returning 400. Require the JSON member and cover both endpoints with an empty-object test.

### `Glosify/Models/ViewModels/AnkiViewModels.cs`

- **Medium — line 66 — Sonar S6964:** omitting `RateAnkiCardForm.ClientToken` produces `Guid.Empty`, which passes model validation. `AnkiStudyService` stores this value under a unique index and treats it as the global idempotency key. The first under-posted rating writes the empty token; later under-posted ratings are silently treated as duplicates. Require a non-empty token at the form/controller or service boundary and add first/repeated under-post tests.

### `Glosify/Services/Ai/Assistant/Tools/SearchBookPagesTool.cs`

- **Medium — lines 105 and 207 — Meziantou MA0011:** the SQL filter lowers page text using provider/database rules, while terms were normalized with `ToLowerInvariant()` and later hit counting/snippet selection use `OrdinalIgnoreCase`. Those three casing regimes can disagree, especially for Turkish dotted/dotless I. A page can be filtered out incorrectly, or pass the filter but receive zero ranked hits, leading the assistant to say that a term is absent. Use one explicit, provider-compatible normalization/collation strategy throughout. Do not simply add `ToLower(CultureInfo)` inside the EF expression because EF Core 10 cannot translate it.

### `Glosify/Services/Ai/Assistant/Tools/SearchWordsTool.cs`

- **Low — lines 52–53 — Meziantou MA0011:** the query term uses invariant lowercase, while `Lemma` and `Translation` use implicit/provider-dependent lowercase. Under the in-memory provider the stored side follows the request culture; under SQL Server it follows the database collation. Identical case-insensitive searches can therefore return different results or false misses. Use the same database-compatible comparison strategy for both sides.

### `Glosify/Services/Ai/Assistant/Tools/ToolArguments.cs`

- **Low — line 95 — Meziantou MA0011:** `GetOptionalInt` parses machine-format numeric strings with the current request culture. Arabic is a supported display culture; under .NET 10, a string such as `+1` can parse in English and fail in Arabic because the culture's sign representation differs. `GetSavedTranscriptTool` then silently changes paging values such as `offset` back to their defaults. Parse with `NumberStyles.Integer` and `CultureInfo.InvariantCulture`.

### `Glosify/Services/Anki/AnkiCollectionService.cs`

- **Medium — lines 40–41, 74, 78, 89, and 94 — Meziantou MA0011:** the service lowercases a canonical language name with the current request culture and compares it with SQL `LOWER(column)`, whose rules come from the database collation. The app supports `tr-TR`; in that culture `Indonesian` becomes `ındonesian`, while a typical SQL collation produces `indonesian`. `ListForLanguageAsync` can hide owned collections, and both ownership checks can return false and cause 404s for the user's own resources. Canonicalize through `QuizLanguageCatalog` and compare stable canonical values/codes without request-culture lowering. The reports at lines 141–142 are not included because both operands there execute under the same SQL collation.

## Test reliability

### `Glosify.BrowserTests/PortfolioJourneys.cs`

- **Low — lines 317 and 329 — JetBrains:** `RenameDialog` and `DeleteDialog` are `async void` event handlers that await `IDialog.AcceptAsync()`. Escaping exceptions are unobserved and can terminate the test process, and the test cannot explicitly await handler completion. Use an observed task/TCS or catch and report exceptions inside the handler.

## Confirmed cleanup findings

These reports are true positives, but no current functional or security impact was demonstrated.

### `Glosify/Controllers/TypingQuizController.cs`

- **Cleanup — line 22 — Sonar S4487 / JetBrains:** `_languageContext` is assigned but never read. Remove the field and constructor dependency.

### `Glosify/Models/Entities/AssistantPendingChange.cs`

- **Cleanup — line 54 — JetBrains:** `AssistantPendingChangeStatus.Rejected` has no references; only `Pending` and `Applied` are used.

### `Glosify/Models/PracticeDirection.cs`

- **Cleanup — line 13 — JetBrains:** `IsValid` has no callers.

### `Glosify/Models/PracticeItemType.cs`

- **Cleanup — lines 13 and 19 — JetBrains:** `IsValid` and `IsWords` have no callers.

### `Glosify/Models/ViewModels/LibraryProjections.cs`

- **Cleanup — line 47 — JetBrains:** `CollectionCard.IsFreestyle` is never read. This is distinct from `QuizCard.IsFreestyle`, which the Razor views do use.

### `Glosify/Models/ViewModels/QuizWorkspaceViewModel.cs`

- **Cleanup — lines 106–108 — JetBrains:** `FlashcardWordViewModel.Id`, `Lemma`, and `Translation` are populated but never read; the views consume the derived `Prompt` and `Answer` fields instead.
- **Cleanup — lines 149 and 151–153 — JetBrains:** `TypingQuizWordViewModel.Id` is populated but never read; `Answer`, `ExampleSentence`, and `ExampleTranslation` are neither populated nor read.

### `Glosify/Models/WordIdList.cs`

- **Cleanup — line 20 — JetBrains:** `Format` has no callers.

### `Glosify/Services/Ai/Assistant/AgentToolContext.cs`

- **Cleanup — line 20 — JetBrains:** `ReplyLanguage` is initialized by `AssistantTurnRunner`, but no assistant tool reads it.

### `Glosify/Services/Ai/Assistant/ChangeApplier.cs`

- **Cleanup — line 14 — Sonar S1144 / JetBrains:** the static `JsonOptions` field is unused.

### `Glosify/Services/Ai/Assistant/Tools/AtomicCustomQuizElementTool.cs`

- **Cleanup — line 19 — Sonar S4487 / JetBrains:** `_context` is assigned but never read. Its base and derived constructors can drop that dependency.

### `Glosify/Services/Ai/Generation/FoundryGenerativeAiClient.cs`

- **Cleanup — line 670 — Sonar S1481 / JetBrains:** the `unavailable` pattern variable is captured but never used; retain the property pattern without binding a variable.

### `Glosify/Services/Ai/Llm/GeminiGenerativeAiClient.cs`

- **Cleanup — line 327 — Sonar S1172 / JetBrains:** `CreateGenerationConfig` does not use `modelName` at any of its three call sites.

### `Glosify/Services/Anki/Fsrs6AnkiScheduler.cs`

- **Cleanup — line 50 — JetBrains:** the initial `state = card.State` value is overwritten in every control-flow branch before it is returned. Declare the local without the dead initializer.

### `Glosify/Services/Auth/DemoAccountSeeder.cs`

- **Cleanup — line 69 — Sonar S1172 / JetBrains:** private `EnsureUserAsync` never uses its cancellation token. Remove it or explicitly check cancellation before invoking the non-cancellable Identity APIs.

### `Glosify/Services/Classrooms/ClassroomLibrary.cs`

- **Cleanup — line 17 — JetBrains:** `GetContentAsync` has no production or test caller; only its interface and implementation exist.

### `Glosify/Services/Classrooms/ClassroomRoster.cs`

- **Cleanup — line 17 — JetBrains:** `GetMembersAsync` has no production or test caller; only its interface and implementation exist.

### `Glosify/Services/Language/LanguageContext.cs`

- **Cleanup — line 6 — JetBrains:** `ILanguageContext.HasLanguage` is never consumed. Removing it also removes corresponding fake implementation boilerplate.

### `Glosify/Services/Language/QuizLanguageCatalog.cs`

- **Cleanup — lines 154 and 181 — JetBrains:** `MaximumCodeLength` and `NormalizeForSearch` have no callers.

### `Glosify/Services/RealtimeTranslation/FoundryTranslationProtocol.cs`

- **Cleanup — line 50 — JetBrains:** every caller discards `TryGetBrowserAudioByteCount`'s output. The method is only used by the test-oriented `IsAllowedBrowserMessage` wrapper; production uses `TryDecodeBrowserAudio` directly.

### `Glosify/Services/RealtimeTranslation/IRealtimeTranslationTranscriptService.cs`

- **Cleanup — line 14 — JetBrains:** `DeleteStaleEmptyAsync` has no caller. Equivalent cleanup logic is already executed inside `RealtimeTranslationService.CleanupStaleSessionsAsync`, leaving the interface member and implementation duplicate dead code.

### `Glosify/Services/RealtimeTranslation/RealtimeTranslationOptions.cs`

- **Cleanup — line 331 — JetBrains:** every caller discards the URI returned by `TryValidateElevenLabsEndpoint`; `BuildEndpoint` independently creates it later. Simplify the validation signature.

### `Glosify/Services/RealtimeTranslation/RealtimeTranslationTelemetry.cs`

- **Cleanup — line 11 — JetBrains:** the `ActivitySource` instance never starts an activity. Keep `ActivitySourceName`, which OpenTelemetry configuration does use, but remove the unused instance unless tracing is added.

### `Glosify/Services/ServiceWarmupMessage.cs`

- **Cleanup — line 7 — JetBrains:** the `Database` message constant has no references.

### `Glosify/Services/Speaking/SpeakingAvatarCatalog.cs`

- **Cleanup — line 286 — JetBrains:** `All` has no references.

### `Glosify/Services/Speaking/SpeakingTelemetry.cs`

- **Cleanup — line 16 — JetBrains:** `SceneProposalsIgnored` is created but never incremented or otherwise referenced.

### `Glosify/Services/Speech/VoiceMap.cs`

- **Cleanup — line 46 — JetBrains:** callers of the three-argument `TryResolve` overload always discard `locale`. The five-argument production overload does use it; simplify only the smaller overload.

### `Glosify/Services/Storage/IBookFileStorage.cs`

- **Cleanup — lines 15 and 19 — JetBrains:** `ExistsAsync` and `GetPropertiesAsync` have no callers. Remove the interface members, Azure implementation methods, and matching test-fake members together.

### `Glosify/Services/Words/IWordService.cs`

- **Cleanup — line 13 — JetBrains:** `sourceLanguage` and `targetLanguage` are unused by the only `AddWordAsync` implementation. Remove both parameters and update callers together.

### `Glosify.LiveSubtitles.Extension/background/service-worker.js`

- **Cleanup — line 1569 — ESLint `no-unused-vars`:** `statusText()` has no callers.

### `Glosify.LiveSubtitles.Extension/lib/chat-buffer.js`

- **Cleanup — line 160 — ESLint `no-useless-escape`:** `\)` is unnecessarily escaped inside the regex character class. Removing the backslash does not change matching behavior.

### `Glosify.LiveSubtitles.Extension/test/realtime-events.test.js`

- **Cleanup — line 20 — ESLint `no-unused-vars`:** `clientTimestamp` is intentionally destructured only to exclude it from `stableFields`, but the local binding itself is never used and fails lint. Preserve the omission with a lint-compatible destructuring/refactor or an explicit ignored-name convention.

### `Glosify.Tests/ClassroomServices.cs`

- **Cleanup — line 38 — JetBrains:** test helper `Call` has no caller.

### `Glosify.Tests/NavigationTests.cs`

- **Cleanup — line 237 — JetBrains:** private helper `AntiForgeryAsync` has no caller.

## Major rejected categories

- Sonar's CSRF findings are not valid here: the shared API base is bearer-only; auth exchanges are PKCE-bound; the relay uses a one-time token; paid status is GET-only; and the Stripe webhook authenticates the raw payload with its signature. Cookie-authenticated mutations remain behind the global antiforgery filter.
- The upload-limit warnings are false positives: the actions set a 26 MiB request limit and the document service enforces 25 MiB.
- The cookie warnings describe intentional local-HTTP support for non-secret preferences; production auth cookies remain secure.
- The Stripe raw-body warning conflicts with required signature verification.
- The reported regular expressions are fixed, escaped, or linear; no catastrophic-backtracking path was found.
- JetBrains' roughly one thousand `_AppLayout.cshtml` syntax/symbol reports and similar Razor findings result from lost Razor parsing context.
- EF tooling, hosting, dependency injection, model binding, JSON, SignalR, and assembly scanning account for the apparently unused context factory, `Program`, request/DTO members, stored JSON models, and assistant tool classes.
- The three C# 14 span-overload diagnostics are non-actionable in this repository. Raw interpreted expression trees can fail, but EF Core 10.0.8 normalizes the current queries; the focused trial-eligibility and telemetry-deletion paths pass.
- Meziantou's `ConfigureAwait(false)`, file/type naming, explicit comparer, string-operator, method-length, collection-abstraction, brace, primary-constructor, `init`, and similar reports are policy/style preferences rather than demonstrated defects. `.editorconfig` already records the ASP.NET Core synchronization-context policy.
- The remaining Sonar under-posting reports fail closed through ownership/not-found checks, represent optional checkbox semantics, or are manually validated by the receiving service.

## Verification

- The complete solution builds with analyzers disabled: 0 warnings and 0 errors.
- The Release .NET test suite passed 1,034 tests (1,025 application tests and 9 browser-project tests) with 4 intentional live-service skips. The one SQL Server-only integration test was excluded because no local SQL Server was available.
- Focused behavioral regression runs passed: 22 Sonar-related tests and 87 culture/search-related tests.
- Extension unit tests passed: 56/56.
- Client unit tests passed: 24/24.
- `npm run lint` passes with no findings.
- The browser-test project builds successfully as part of the solution build; browser journeys were not executed because they require a running application and browser environment.
