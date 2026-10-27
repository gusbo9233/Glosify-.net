# Analyzer findings compared with current files

Compared against HEAD `064adc535487ff3a6ef71a528ffd18feaf10b564`; generated 2026-10-27T13:14:19.648461+00:00.

All **3,844 exported finding records** were checked. **664 current default-branch findings in 68 files** refer to files changed since their recorded baseline. **30** overlap edited reported ranges; **302** other findings have unchanged reported text that moved to a different line number.

These counts include overlapping reports from different analyzers; they are not counts of unique defects. A changed file or edited range is not proof that a finding has been fixed.

| Analyzer | Exported findings | Findings in changed files, all statuses/refs | Files, all statuses/refs | Current default-branch findings in changed files | Current findings with edited reported ranges |
| --- | ---: | ---: | ---: | ---: | ---: |
| DeepSource | 1882 | 364 | 50 | 364 | 14 |
| Codacy | 1517 | 283 | 49 | 283 | 14 |
| Semgrep | 67 | 16 | 4 | 6 | 0 |
| SonarQube | 378 | 11 | 8 | 11 | 2 |

Across all exported statuses/refs: **674 records in 70 changed files**. The extra 10 are Semgrep findings already marked `fixed` in two workflow files. Semgrep also contains 8 pull-request findings and 3 provisionally ignored findings; their source files are unchanged against their linked snapshots, and they are excluded from the current default-branch counts.

## Baselines and scope

- DeepSource records repository commit `87a8e6c887a3a36b39496e116c5083d2ba643b10` in its export metadata.
- Codacy explicitly analyzed `87a8e6c887a3a36b39496e116c5083d2ba643b10`. Its per-issue `commitInfo` sometimes points to older originating commits, so it was not used as the scan baseline.
- SonarQube analysis `430b569d-c2c1-41b5-a02e-8c85fff222ef` at `2026-10-27T12:58:38+0000` confirms revision `87a8e6c887a3a36b39496e116c5083d2ba643b10`.
- Semgrep was compared per finding using its linked source revision: `87a8e6c` for master open/ignored findings, `4c7a74e` for PR 141 findings, and `1613969` for older fixed findings.
- The source working tree had no uncommitted changes; the newer source edits were already committed. Comparison uses the current file contents, not modification timestamps.
- Existing paths and all reported ranges on changed files could be mapped. No missing or deleted finding files were found. Newly added files are absent from these old exports.

## Examples checked against the diff

- **Removed constants:** Codacy `SonarCSharp_S2339` references `WorkspacePageSize` in `Glosify/Services/Anki/AnkiCollectionService.cs` (old line 12), and `NewCardsPerDay` / `MaximumReviewsPerDay` in `Glosify/Models/AnkiStudyDefaults.cs` (old lines 7–8). Those declarations are removed; the old findings no longer identify existing declarations. Other constants still exist and have not been assessed for the same rule.
- **Anki synchronization moved:** DeepSource complexity and Codacy length warnings reference the former `SyncCollectionAsync` implementation at old line 504. It is now a short wrapper at line 499; the body was moved and changed in `SyncCollectionCoreAsync` at line 506. The previous numerical measurements should not be applied to the wrapper, and the new core needs separate analysis.
- **Language-cookie finding moved:** SonarQube `S2092` references old line 66 in `LanguageContext.cs`. Cookie options now come from a helper at lines 122–129. The expression `Secure = context.Request.IsHttps` remains. This is evidence of relocation, not evidence of a security fix.
- **Semgrep controller findings have unchanged reported ranges:** the six current findings in `FlashcardQuizController.cs` and `TypingQuizController.cs` are in files whose only change since their scan was removal of `ProcessingStatus`. Their reported action declarations/ranges were not edited; those file changes do not establish that the findings were addressed.
- **Anki workspace test changed:** SonarQube points to the SQL Server test declaration at old line 21 in `AnkiWorkspaceTests.cs`. That test was renamed and its shared verification body changed. This report records the source change only; assertion quality and the rule verdict were not evaluated.
- **Tool error caveat:** Codacy's `Stylelint_scss_function-disallowed-list` report for `anki.css:1` says the rule is unknown. Line 1 changed, but that does not establish that the underlying analyzer configuration error is fixed.

## Current findings whose reported ranges were edited

Current anchors prefixed with `~` are approximate starts of changed blocks. They must not be interpreted as exact remapped finding locations. `removed` means the reported old line has no direct surviving mapping.

| Analyzer | File | Old line | Current anchor | Rule | Finding |
| --- | --- | ---: | ---: | --- | --- |
| DeepSource | `Glosify.Tests/AnkiCollectionServiceTests.cs` | 184 | ~185 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/AnkiCollectionServiceTests.cs` | 239 | ~240 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/AnkiCollectionServiceTests.cs` | 543 | ~614 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/AnkiMembershipConcurrencyTests.cs` | 23 | ~23 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| Codacy | `Glosify.Tests/AnkiMembershipConcurrencyTests.cs` | 94 | ~94 | `SonarCSharp_S1172` | Remove this unused method parameter 'eventData'. |
| DeepSource | `Glosify.Tests/AnkiWorkspaceTests.cs` | 13 | ~13 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/AnkiWorkspaceTests.cs` | 21 | ~21 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| SonarQube | `Glosify.Tests/AnkiWorkspaceTests.cs` | 21 | ~21 | `csharpsquid:S2699` | Add at least one assertion to this test case. |
| DeepSource | `Glosify.Tests/AssistantLearningTests.cs` | 448 | ~448 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/AssistantLearningTests.cs` | 511 | ~516 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/AssistantRunTests.cs` | 536 | ~536 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/DisplayLocalizationTests.cs` | 422 | ~421 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/SelectedLanguageFilterTests.cs` | 15 | ~15 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| DeepSource | `Glosify.Tests/SelectedLanguageFilterTests.cs` | 31 | ~29 | `CS-R1073` | Method with return type `Task` does not follow the naming convention |
| Codacy | `Glosify.Tests/WordServiceAnkiAtomicityTests.cs` | 130 | ~129 | `SonarCSharp_S1172` | Remove this unused method parameter 'userId'. |
| Codacy | `Glosify/Controllers/AnkiController.cs` | 38 | ~38 | `SonarCSharp_S2360` | Use the overloading mechanism instead of the optional parameters. |
| Codacy | `Glosify/Controllers/AnkiController.cs` | 139 | ~148 | `SonarCSharp_S2360` | Use the overloading mechanism instead of the optional parameters. |
| Codacy | `Glosify/Models/AnkiStudyDefaults.cs` | 7 | ~7 | `SonarCSharp_S2339` | Change this constant to a 'static' read-only property. |
| Codacy | `Glosify/Models/AnkiStudyDefaults.cs` | 8 | ~7 | `SonarCSharp_S2339` | Change this constant to a 'static' read-only property. |
| Codacy | `Glosify/Services/Ai/Assistant/Runtime/AssistantRunExecutor.cs` | 118 | ~118 | `Lizard_nloc-medium` | Method AssistantRunExecutor::InitializeAsync has 53 lines of code (limit is 50) |
| Codacy | `Glosify/Services/Anki/AnkiCollectionService.cs` | 12 | removed | `SonarCSharp_S2339` | Change this constant to a 'static' read-only property. |
| Codacy | `Glosify/Services/Anki/AnkiCollectionService.cs` | 163 | ~163 | `SonarCSharp_S2360` | Use the overloading mechanism instead of the optional parameters. |
| Codacy | `Glosify/Services/Anki/AnkiCollectionService.cs` | 504 | ~499 | `SonarCSharp_S2360` | Use the overloading mechanism instead of the optional parameters. |
| Codacy | `Glosify/Services/Anki/AnkiCollectionService.cs` | 504 | ~499 | `Lizard_nloc-medium` | Method AnkiCollectionService::SyncCollectionAsync has 83 lines of code (limit is 50) |
| DeepSource | `Glosify/Services/Anki/AnkiCollectionService.cs` | 504 | ~499 | `CS-R1140` | SyncCollectionAsync has a cyclomatic complexity of 27 with "very-high" risk |
| Codacy | `Glosify/Services/Anki/AnkiCollectionService.cs` | 678 | ~732 | `Lizard_parameter-count-medium` | Method AnkiCollectionService::GetOrCreateNote has 11 parameters (limit is 8) |
| Codacy | `Glosify/Services/Anki/IAnkiCollectionService.cs` | 87 | ~86 | `SonarCSharp_S2360` | Use the overloading mechanism instead of the optional parameters. |
| SonarQube | `Glosify/Services/Language/LanguageContext.cs` | 66 | ~98 | `csharpsquid:S2092` | Set the 'Secure' flag on this cookie. |
| Codacy | `Glosify/wwwroot/css/anki.css` | 1 | ~1 | `Stylelint_scss_function-disallowed-list` | Unknown rule scss_function-disallowed-list. Did you mean function-disallowed-list? |
| DeepSource | `Glosify/wwwroot/js/anki.js` | 4 | ~5 | `JS-R1005` | `initialize` has a cyclomatic complexity of 8 with "medium" risk |

## All updated files with current findings

DS = DeepSource; C = Codacy; SG = Semgrep; SQ = SonarQube. Counts are finding records. The last column counts records with edited reported ranges.

| File | DS | C | SG | SQ | Edited range |
| --- | ---: | ---: | ---: | ---: | ---: |
| `AGENTS.md` | 0 | 1 | 0 | 0 | 0 |
| `Glosify.BrowserTests/PortfolioJourneys.GuestBrowsing.cs` | 3 | 3 | 0 | 0 | 0 |
| `Glosify.ClientTests/anki-settings.test.js` | 1 | 0 | 0 | 0 | 0 |
| `Glosify.ClientTests/assistant-runs.test.js` | 1 | 1 | 0 | 0 | 0 |
| `Glosify.Tests/AccountedQuizDeletionTests.cs` | 3 | 4 | 0 | 0 | 0 |
| `Glosify.Tests/AiCreditServiceTests.cs` | 44 | 6 | 0 | 0 | 0 |
| `Glosify.Tests/AnkiCollectionServiceTests.cs` | 28 | 2 | 0 | 0 | 3 |
| `Glosify.Tests/AnkiControllerLanguageTests.cs` | 15 | 7 | 0 | 0 | 0 |
| `Glosify.Tests/AnkiMembershipBatchTests.cs` | 2 | 3 | 0 | 0 | 0 |
| `Glosify.Tests/AnkiMembershipConcurrencyTests.cs` | 4 | 1 | 0 | 0 | 2 |
| `Glosify.Tests/AnkiPageTests.cs` | 0 | 6 | 0 | 0 | 0 |
| `Glosify.Tests/AnkiWorkspaceTests.cs` | 2 | 0 | 0 | 1 | 3 |
| `Glosify.Tests/AssistantChatTests.cs` | 13 | 1 | 0 | 0 | 0 |
| `Glosify.Tests/AssistantCollaboratorTests.cs` | 3 | 11 | 0 | 0 | 0 |
| `Glosify.Tests/AssistantHarness.cs` | 0 | 4 | 0 | 0 | 0 |
| `Glosify.Tests/AssistantLearningManagementTests.cs` | 19 | 0 | 0 | 0 | 0 |
| `Glosify.Tests/AssistantLearningTests.cs` | 24 | 1 | 0 | 0 | 2 |
| `Glosify.Tests/AssistantReviewRegressionTests.cs` | 20 | 5 | 0 | 0 | 0 |
| `Glosify.Tests/AssistantRunApiTests.cs` | 8 | 0 | 0 | 0 | 0 |
| `Glosify.Tests/AssistantRunTests.cs` | 34 | 1 | 0 | 0 | 1 |
| `Glosify.Tests/AvatarTests.cs` | 18 | 3 | 0 | 0 | 0 |
| `Glosify.Tests/CollectionServiceDeleteTests.cs` | 2 | 1 | 0 | 0 | 0 |
| `Glosify.Tests/DisplayLocalizationTests.cs` | 14 | 1 | 0 | 0 | 1 |
| `Glosify.Tests/LayoutViewComponentTests.cs` | 8 | 2 | 0 | 0 | 0 |
| `Glosify.Tests/QuizContentSelectionTests.cs` | 5 | 0 | 0 | 0 | 0 |
| `Glosify.Tests/QuizJsonImportServiceTests.cs` | 12 | 12 | 0 | 0 | 0 |
| `Glosify.Tests/QuizServiceDeleteTests.cs` | 3 | 2 | 0 | 0 | 0 |
| `Glosify.Tests/QuizSessionLimitTests.cs` | 1 | 0 | 0 | 0 | 0 |
| `Glosify.Tests/ResourceAccountingTests.cs` | 16 | 3 | 0 | 0 | 0 |
| `Glosify.Tests/SelectedLanguageFilterTests.cs` | 3 | 0 | 0 | 0 | 2 |
| `Glosify.Tests/TypingQuizServiceTests.cs` | 6 | 0 | 0 | 0 | 0 |
| `Glosify.Tests/WordServiceAnkiAtomicityTests.cs` | 2 | 30 | 0 | 0 | 1 |
| `Glosify.Tests/WordServiceSentencePracticeTests.cs` | 4 | 2 | 0 | 0 | 0 |
| `Glosify/Controllers/AnkiController.cs` | 0 | 7 | 0 | 0 | 2 |
| `Glosify/Controllers/FlashcardQuizController.cs` | 5 | 9 | 3 | 0 | 0 |
| `Glosify/Controllers/QuizController.cs` | 1 | 15 | 0 | 0 | 0 |
| `Glosify/Controllers/TypingQuizController.cs` | 1 | 7 | 3 | 0 | 0 |
| `Glosify/Extensions/AuthenticationExtensions.cs` | 0 | 1 | 0 | 0 | 0 |
| `Glosify/Infrastructure/Api/ApiExceptionMapper.cs` | 1 | 0 | 0 | 0 | 0 |
| `Glosify/Migrations/GlosifyContextModelSnapshot.cs` | 1 | 0 | 0 | 0 | 0 |
| `Glosify/Models/AnkiStudyDefaults.cs` | 0 | 3 | 0 | 0 | 2 |
| `Glosify/Services/Ai/Assistant/AssistantCollaborators.cs` | 0 | 1 | 0 | 0 | 0 |
| `Glosify/Services/Ai/Assistant/AssistantThreadStore.cs` | 0 | 2 | 0 | 0 | 0 |
| `Glosify/Services/Ai/Assistant/Runtime/AssistantRunContext.cs` | 1 | 0 | 0 | 0 | 0 |
| `Glosify/Services/Ai/Assistant/Runtime/AssistantRunExecutor.cs` | 5 | 12 | 0 | 0 | 1 |
| `Glosify/Services/Ai/Assistant/Runtime/AssistantRunModels.cs` | 0 | 0 | 0 | 1 | 0 |
| `Glosify/Services/Ai/Assistant/Runtime/AssistantRunStore.cs` | 1 | 6 | 0 | 1 | 0 |
| `Glosify/Services/Ai/Assistant/Tools/ManageLearningTools.cs` | 1 | 2 | 0 | 0 | 0 |
| `Glosify/Services/Anki/AnkiCollectionService.Assistant.cs` | 1 | 8 | 0 | 0 | 0 |
| `Glosify/Services/Anki/AnkiCollectionService.Maintenance.cs` | 0 | 4 | 0 | 0 | 0 |
| `Glosify/Services/Anki/AnkiCollectionService.Membership.cs` | 2 | 4 | 0 | 0 | 0 |
| `Glosify/Services/Anki/AnkiCollectionService.cs` | 5 | 14 | 0 | 0 | 6 |
| `Glosify/Services/Anki/AnkiStudyService.cs` | 1 | 4 | 0 | 0 | 0 |
| `Glosify/Services/Anki/IAnkiCollectionService.cs` | 0 | 29 | 0 | 0 | 1 |
| `Glosify/Services/Guest/GuestSamples.ViewModels.cs` | 1 | 2 | 0 | 0 | 0 |
| `Glosify/Services/Language/LanguageContext.cs` | 0 | 0 | 0 | 2 | 1 |
| `Glosify/Services/Quizzes/QuizJsonImportService.cs` | 3 | 11 | 0 | 0 | 0 |
| `Glosify/Services/Quizzes/QuizService.cs` | 0 | 10 | 0 | 0 | 0 |
| `Glosify/Views/Quiz/Index.cshtml` | 0 | 0 | 0 | 2 | 0 |
| `Glosify/Views/Shared/Components/AssistantPanel/Default.cshtml` | 0 | 0 | 0 | 1 | 0 |
| `Glosify/Views/Shared/_AppLayout.cshtml` | 0 | 0 | 0 | 2 | 0 |
| `Glosify/wwwroot/css/anki.css` | 0 | 1 | 0 | 0 | 1 |
| `Glosify/wwwroot/css/quiz-library.css` | 0 | 1 | 0 | 0 | 0 |
| `Glosify/wwwroot/js/anki.js` | 2 | 0 | 0 | 0 | 1 |
| `Glosify/wwwroot/js/assistant.js` | 1 | 14 | 0 | 1 | 0 |
| `Glosify/wwwroot/js/assistant/runs.js` | 1 | 0 | 0 | 0 | 0 |
| `Glosify/wwwroot/js/flashcard-quiz.js` | 8 | 3 | 0 | 0 | 0 |
| `README.md` | 4 | 0 | 0 | 0 | 0 |

## Verification and files

Checked all 3,844 unique analyzer/issue identifiers, matched every file, verified 634 unchanged reported ranges in updated files against both snapshots, verified CSV record counts, and confirmed HEAD did not change during the comparison. No source code was modified; no new scans or application tests were run.

[Complete per-finding CSV](findings-vs-current-files.csv) · [Complete JSON, baselines, and validation](findings-vs-current-files.json)

The CSV/JSON retains all exported statuses and refs, with `active_default_branch` to filter the current set, `file_state` for file changes, and `range_state` for overlap. Exact unchanged-text mappings use `current_start_line`; edited blocks use the separate approximate `changed_block_start_line`.
