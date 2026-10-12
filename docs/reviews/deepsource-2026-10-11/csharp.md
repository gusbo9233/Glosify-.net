# DeepSource C# triage

Reviewed all **895 occurrences across 33 rules** from `csharp-full.json`, against analyzed `origin/master` commit **ef9cc38e6806531f7cd586e24c04460b2384f79e**, using the exported default-source tree. The current working branch differs substantially; files absent locally are **not** treated as stale DeepSource findings. Every occurrence has an ID-preserving decision and the actual reported source line in [issues.csv](issues.csv).

Disposition totals: **884 no fix, 10 defer, 1 fix**. The one proposed fix is a manually confirmed defect found while reviewing a complexity hotspot; DeepSource itself did not diagnose that defect. No reported C# “critical” alert established a current critical defect.

## Recommended fix: cache token arithmetic can overflow validation

**P2 — `Glosify/Controllers/GameUsageController.cs:23`.** This is adjacent to CS-R1140 at line 16. `GameUsageEvent.CachedInputTokens` and `CacheWriteTokens` are nullable `long`s. The controller checks they are nonnegative, then sums them without first bounding each operand:

```csharp
(input.CachedInputTokens ?? 0) + (input.CacheWriteTokens ?? 0) > input.InputTokens
```

With both cache counts `long.MaxValue` and input count 100, the sum wraps to -2, so the request passes. A review-only HTTP integration test sent this payload with the valid test service key and the `decisions` endpoint, received **204**, then read back both impossible counts from the database. Choosing `decisions` also confirms this is not merely an exception thrown by the separate response-pricing calculation.

The endpoint requires its server-to-server service key; this is **trusted-input validation and accounting integrity**, not an unauthenticated public exploit. No production requests were made.

Recommended fix: independently bound cached/read/write counts to the same maximum as total input, and enforce `cached + write <= input` only with bounded operands; explicitly decide how absent total input interacts with supplied cache counts. Keep the existing service authentication and response contract. Add cases for `long.MaxValue`, each individual count exceeding input, sum exceeding input, negative counts, valid totals at the boundary, and legitimate unknown measurements. Rejected HTTP requests must return 400 and persist no usage/session rows.

Reproducer: [DeepSourceTriageProbeTests.cs.txt](evidence/DeepSourceTriageProbeTests.cs.txt), review artifact only; it intentionally asserts the current incorrect acceptance. **1 passed**, proving the defect. The production repository was not edited.

## Optional work, not an immediate bug-fix queue

- **CS-R1114, 2 reports for one expression:** `GameSecurity.cs:33-34`. Converting nested authorization status selection to guard clauses would make review easier. Preserve 404 when SharedAuth is disabled, 401 for invalid/not-allowed/locked users, 403 for non-admins, 200 otherwise. Existing GameAccessTests define behavior. No bypass found.
- **CS-R1140, 6 refactor candidates:** `ScribeTranslationRelay.cs:48`, `RealtimeTranslationService.cs:125`, `AdaptivePartialTranslationScheduler.cs:45`, `AvatarConversation.cs:17`, `AnkiCollectionService.cs:539`, `LearningTools.cs:73`. These combine lifecycle management or multiple query/selection decisions. Extract cohesive helpers when changing the feature; preserve disposal order, cancellation, ownership, reservation settlement, history state and atomic writes. Do not add layers or split solely to reduce a metric.
- **CS-W1092, 2 test-only reports:** `AssistantAnalyticsControllerTests.cs:39`, `AdminAuthorizationTests.cs:176`. Use `CultureInfo.InvariantCulture` or direct `DateTimeOffset` construction if touching these fixtures. Dates are fixed ISO strings; no actual locale failure was demonstrated and this is not a critical production issue.

## Why the high-severity alerts should not be “fixed” mechanically

- **CS-R1006, 1:** `AnkiCollectionService.Maintenance.cs:30-39` returns `ExecuteAtomicallyAsync(...)`. Its `return null` belongs to an **async lambda** representing a no-op rename result. `ExecuteAtomicallyAsync` itself is async (`AnkiCollectionService.cs:656`). It returns a non-null task containing a null result. Wrapping the nested null in `Task.FromResult` would be the wrong fix.
- **CS-W1094, 3:** `QuizSessionRegistry.cs:55,88,110` locks the shared list retrieved from a `ConcurrentDictionary`, not an unshared new local. Register rechecks dictionary identity after acquiring the lock and retries if it raced with removal. The relevant registry and lock tests passed.
- **CS-W1096, 7:** six are `byte[] RowVersion` EF entity properties, each configured with `IsRowVersion` in `Data/Configurations`. That is the documented SQL Server concurrency-token representation. `RequestResourceReservations.Pending` returns a fresh snapshot under lock; mutating that returned array cannot mutate the internal list. [EF Core concurrency guidance](https://learn.microsoft.com/en-us/ef/core/modeling/concurrency).
- **CS-W1100, 18:** 17 are `using`/`await using` declarations with purposeful acquisition, lifetime/disposal, or test-host initialization. Production examples are `RealtimeTranslationService.cs:768`, `QuizJsonImportService.cs:73`, `BookPageTranslationService.cs:104`. Removing those “unused” leases breaks serialization. The remaining `AssistantRunRegressionTests.cs:111` stores an unused run result; only its binding is optional, and the awaited start must remain.
- **CS-W1061, 1:** `Program.cs:530` exposes the generated global entry point using `public partial class Program { }` for the integration host. The [ASP.NET Core 10 integration-testing documentation](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0) shows this exact pattern.
- **CS-R1134, 1:** `AssistantTool.cs:168` is an abstract `ToolEffect` record with three concrete effect records. The executor handles each concrete case; the empty root is intentional type structure.
- **CS-W1000, 1:** `DisplayLocalizationTests.cs:244` unnecessarily puts `$` before a fixed expected string. Its value and assertion are correct; cosmetic cleanup, not a critical issue.

Other especially misleading advice:

- **CS-R1078:** `ApiControllerBase.cs:15` is intentionally an empty abstract **class** carrying inherited MVC, bearer-auth and antiforgery metadata and deriving `ControllerBase`. An interface is not equivalent.
- **CS-R1136:** `AdminController.cs:192-193` is an EF IQueryable predicate. Explicit null guards are valid here. C# expression trees prohibit null-propagation and pattern matching syntax; accepting the suggested rewrite can prevent compilation. [Expression-tree restrictions](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/concepts/expression-trees/).
- **CS-R1131:** all 34 report already-grouped decimal literals such as `1_000_000m` and `10_000_000_000_000m`. There is no bad monetary scale or grouping demonstrated; the decimal suffix appears to confuse this analyzer.
- **CS-S1001:** all 3 insecure-protocol strings belong to negative tests that assert rejection of insecure endpoints. Removing them reduces security coverage.
- **CS-R1062:** all 29 namespace warnings are in generated EF migrations/model snapshot. `.editorconfig` explicitly marks this directory generated and disables analyzer/style severity there.
- **CS-R1073:** 653 async-suffix suggestions comprise **620 .NET test methods/helpers, 29 browser-test methods/helpers and only 4 private/internal production helpers**. The production names are `ReadClientFrame`, and `AssistantRunHttp.Run`, `Undo`, `Events`. No public async API defect. Tests intentionally use descriptive scenario names. No bulk rename.
- **CS-W1022:** all 35 declarations are assigned on every reachable use (branches, try/catch, do/while and stream loops). The reviewed snapshot compiled successfully, exercising C# definite-assignment checks.
- **CS-R1008:** catches either filter specific transport/cancellation failures or provide intentional cleanup/maintenance boundaries. `AvatarSpeech.cs:54` drains a sender already observed in the receive loop and then settles billing. `ResourceMaintenanceService.cs:144` retains cleanup requests/capacity after failed deletes and logs a count. Do not remove cleanup merely to avoid catching `Exception`.
- **CS-W1009:** current missing-default switches intentionally ignore irrelevant kinds (`ChangeApplier` projects sentence changes only, history extracts supported parts, schema recursion handles containers), or let null effects proceed to change application. All currently declared ToolEffect variants are handled. No forced throwing default.

## Verification and limits

Ran in the isolated analyzed snapshot:

```sh
dotnet test Glosify.Tests/Glosify.Tests.csproj -c Release --filter 'FullyQualifiedName~QuizSessionRegistryTests|FullyQualifiedName~ReferenceCountedKeyedAsyncLockTests|FullyQualifiedName~GlosifyBlobServiceClientTests|FullyQualifiedName~RealtimeTranslationOptionsTests|FullyQualifiedName~RequestResourceReservationsTests' --logger 'console;verbosity=minimal'
```

Build succeeded. **60 tests passed, 0 failed, 0 skipped.** Initial VSTest startup could not bind its loopback communication socket under the sandbox; the identical focused test run passed after tool-approved escalation. Restore used cached dependencies; NuGet vulnerability-feed access failed with NU1900, so this was not a successful fresh dependency audit.

The separate HTTP overflow reproducer also passed (**1 test**). No full test suite, SQL Server integration suite, live provider call or production behavior was exercised. Other decisions combine source/caller/configuration inspection, the successful compiler checks, existing test intent and primary framework documentation; they are not blanket proof that each feature is free of unrelated defects.

## Rule-by-rule coverage

| Rule | Count | Decision | Evidence / reason |
|---|---:|---|---|
| CS-A1003 | 1 | 1 no_fix | NotImplementedException is in a test stub for an unused interface method, deliberately fail-fast if the test unexpectedly invokes it. |
| CS-P1009 | 1 | 1 no_fix | Generated historical EF migration allocates an empty rowversion default once; not a meaningful runtime performance defect. Do not hand-edit migration for style. |
| CS-P1026 | 4 | 4 no_fix | Returns a safe snapshot for concurrent enumeration. Production pending-reservations snapshot is taken under a lock; test properties snapshot concurrent queues. Removing copies would harm correctness. |
| CS-R1006 | 1 | 1 no_fix | False positive: outer method returns ExecuteAtomicallyAsync task. Null is the nullable result returned inside an async lambda, never a null Task. |
| CS-R1008 | 8 | 8 no_fix | Filtered transport/cancellation catches or deliberate teardown/maintenance boundaries. Callers observe outcomes; clean-up must settle reservations and failed blobs retain capacity. No swallowed user-visible failure demonstrated. |
| CS-R1033 | 9 | 9 no_fix | Negated Any expresses absence/existence directly, often inside EF IQueryable predicates. All inversion is equivalent style with no measured benefit. |
| CS-R1039 | 3 | 3 no_fix | Nested guards are intentional readable control flow; merging conditions is optional style. |
| CS-R1048 | 3 | 3 no_fix | Test setup assignments can use object initializers but behavior is unchanged. |
| CS-R1050 | 1 | 1 no_fix | Scoped await-using disposes upload stream and timeout at the intended block boundary. Keep explicit resource lifetime. |
| CS-R1062 | 29 | 29 no_fix | All occurrences are generated EF migrations/snapshot, which .editorconfig marks generated and excludes from style analysis. |
| CS-R1068 | 2 | 2 no_fix | Lambdas invoking DateOnly.AddDays can be method groups but no behavior/performance problem is demonstrated. |
| CS-R1073 | 653 | 653 no_fix | Test method naming convention or private/internal helper naming only. Async suffix does not affect awaiting or execution. No bulk rename justified. |
| CS-R1076 | 5 | 5 no_fix | Readable-number formatting only (FNV constants and boundary-test decimal literals); preserve values, edit opportunistically. |
| CS-R1078 | 1 | 1 no_fix | ApiControllerBase carries inherited MVC, bearer authorization and antiforgery metadata and derives ControllerBase. Converting to an interface would remove the intended controller base behavior. |
| CS-R1085 | 1 | 1 no_fix | Block lambda around nested authorization-policy registration is clear and correct. Expression body is optional style. |
| CS-R1114 | 38 | 36 no_fix, 2 defer | Nested conditional chooses a small value/branch mapping; no incorrect branch demonstrated. Readability changes only when locally useful. Two reports identify one nested authorization status expression. Optional readable guard clauses, preserving exact 404/401/403/200 ordering and existing GameAccessTests; no auth bypass demonstrated. |
| CS-R1116 | 1 | 1 no_fix | Recursive JSON schema walker naturally switches on object/array type; scalars and null intentionally need no work. |
| CS-R1118 | 2 | 2 no_fix | Nullable bool coalescing explicitly treats null as false; comparison to true is equivalent style. |
| CS-R1131 | 34 | 34 no_fix | Reported decimal literals already use standard three-digit groups, e.g. 1_000_000m; suffix appears to confuse analyzer. Do not alter correct monetary scale constants. |
| CS-R1134 | 1 | 1 no_fix | Empty abstract ToolEffect record is the intentional root of AskUserEffect, UpdatePlanEffect and ReadSourceEffect; executor pattern-matches its concrete cases. |
| CS-R1136 | 2 | 2 no_fix | Explicit null guards are required in this EF IQueryable expression tree; suggested null propagation/pattern syntax is unsupported in expression trees. |
| CS-R1140 | 14 | 6 defer, 7 no_fix, 1 fix | Optional refactor when changing this feature: extract cohesive lifecycle/query decisions while preserving cancellation, ownership, billing and rollback behavior. Complexity alone does not warrant immediate rewriting. Complexity threshold alone does not show a defect. Validators and dispatch tables must enumerate domain cases; preserve checks, ownership and transaction boundaries. Confirmed HTTP integration repro: CachedInputTokens=CacheWriteTokens=long.MaxValue, InputTokens=100 passes line23 because sum overflows to -2. Endpoint returns 204 and persists impossible counts. Bound cache counts before arithmetic and verify sum cannot exceed input; preserve service authentication. Complexity warning itself did not identify overflow. |
| CS-R1141 | 1 | 1 no_fix | Private exception-translation helper takes a token for inspection, not an async operation public contract. Last-position preference is cosmetic. |
| CS-S1001 | 3 | 3 no_fix | Insecure URL strings are negative security-test inputs asserting production rejects non-HTTPS endpoints. Keep them. |
| CS-W1000 | 1 | 1 no_fix | Test expected literal contains an unnecessary interpolation marker; value and assertion behavior are correct. Remove only when touching this test. |
| CS-W1004 | 4 | 4 no_fix | Private/internal exceptions are caught/mapped within the same implementation/assembly; exposing them publicly adds unnecessary API surface. |
| CS-W1009 | 6 | 6 no_fix | Switch intentionally handles relevant cases and ignores others, or null falls through to normal change application. No missing currently-supported case found. |
| CS-W1022 | 35 | 35 no_fix | Locals are assigned before every use (branch/try/do-while patterns). C# definite-assignment compiler checks apply; build succeeded. |
| CS-W1061 | 1 | 1 no_fix | Global public partial Program exposes the top-level ASP.NET entry point to WebApplicationFactory<Program>, the documented integration-test pattern. |
| CS-W1092 | 2 | 2 defer | Fixed ISO test dates are not a demonstrated production defect. Explicit invariant culture or direct DateTimeOffset construction is inexpensive optional test hardening. |
| CS-W1094 | 3 | 3 no_fix | Local variable aliases a shared per-user list stored in ConcurrentDictionary; lock is on the shared object. Registration verifies dictionary identity after taking the lock. |
| CS-W1096 | 7 | 7 no_fix | EF entities expose byte[] RowVersion configured with IsRowVersion; remaining property is a defensive array snapshot. Do not replace SQL concurrency tokens. |
| CS-W1100 | 18 | 18 no_fix | Using/await-using declarations intentionally acquire and dispose leases/streams/transactions or initialize test hosts; no subsequent variable reference is required. The local run result is unused, but awaiting StartAsync is required for test setup. Optionally remove only the variable binding; never remove the call. |
