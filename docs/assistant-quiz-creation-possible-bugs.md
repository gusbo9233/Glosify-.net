---
title: "Assistant-to-Quiz Trace: Possible Bugs and Risks"
date: "2026-08-11"
---

# Assistant-to-Quiz Trace: Possible Bugs and Risks

This is the working bug log requested alongside the debugger trace. It is grounded in
repository commit `1c817ee66c7673b1dc90bfbe397bb188d59539ed`, focused tests, and a
read-only Azure snapshot from 2026-08-11. It reports findings; it does not change
application code, schemas, configuration, or infrastructure.

The line-by-line evidence and successful-path boundary are in the companion
[Assistant-to-Quiz Debugger Trace](assistant-quiz-creation-debugger-trace.md).

Classification:

- **Confirmed code-path defect** - the current source necessarily produces the described
  result for the stated inputs.
- **High-confidence risk** - the race/failure follows from the code, but reproducing it
  needs concurrency, a process interruption, or fault injection.
- **Behavior/configuration mismatch** - two documented or coded intentions disagree.
- **Hardening/operations opportunity** - not a failure in the successful trace, but a
  material reliability/security/capacity concern.

Severity means impact, not certainty:

- **High** - can lose or irrecoverably mis-state a proposal, or duplicate durable data.
- **Medium** - user-visible failure/race, material reproducibility risk, or production
  reliability/security gap without demonstrated data loss in the healthy path.
- **Low** - documentation/behavior mismatch or observability improvement with limited
  immediate runtime impact.

## Summary

| ID | Classification | Severity | Finding |
|---|---|---:|---|
| B1 | Confirmed defect | Medium | post-Apply browser selector refresh always uses the wrong auth scheme |
| B2 | Confirmed failure-path defect | High | a later Foundry failure can persist “queued” history but lose the proposal |
| B3 | High-confidence crash-consistency risk | High | process loss after status claim can leave Applied with no quiz |
| B4 | Confirmed defect | Medium | stale collection can return 200 Applied (0) and permanently consume proposal |
| B5 | Behavior mismatch | Low | real Auto path cannot use an authored agent's model default |
| B6 | High-confidence concurrency risk | Medium | concurrent Sends can calculate the same message sequence |
| B7 | High-confidence client race | Medium | context persistence is fire-and-forget |
| B8 | Configuration-boundary risk | Medium | live authored tools replace profile tools without a runtime profile allowlist |
| B9 | Low-frequency transaction risk | Medium | ambiguous commit retry can create a duplicate quiz |
| B10 | Operations issue | Medium | App Service Health Check is not configured despite a health endpoint |
| B11 | Reliability/capacity posture | Medium | one B1 worker and a low-capacity Basic SQL tier have unmeasured headroom |
| B12 | Security hardening | Medium | SQL and Foundry network/local-auth posture is broader than least privilege |
| B13 | Documentation defect | Low | serverless/Windows claims disagree with live Basic/Linux resources |
| B14 | Reproducibility risk | Medium | automatic model-version upgrade can change behavior without an app/agent change |
| B15 | Observability gap | Low | App Service platform/resource logs are not routed by a diagnostic setting |

## B1 - Post-Apply quiz-selector refresh is unauthorized

**Classification:** confirmed code-path defect.
**Creation result:** unaffected; Q and its words are already committed.
**Evidence:** [`assistant.js:649-677`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/wwwroot/js/assistant.js#L649-L677),
[`ApiControllerBase.cs:7-15`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Controllers/Api/ApiControllerBase.cs#L7-L15)

The cookie-authenticated browser calls `GET /api/quizzes` with only `Accept`. The target
controller explicitly authorizes only `Identity.Bearer`. The web Identity cookie therefore
does not satisfy that policy. ASP.NET Core returns 401 before `QuizzesApiController.List`,
and JavaScript silently returns on `!response.ok`.

**User-visible effects**

- the new quiz is not added to or selected in the assistant selector;
- `setQuizContext` is not called, so the thread remains at the library root;
- the link still renders, making the main operation appear successful;
- the link is DOM-only and disappears on a history reload.

**Minimal reproduction**

1. Sign in through the normal web UI.
2. Create and Apply a library-root quiz through the assistant.
3. Inspect Network: `/Assistant/Apply/...` is 200, followed by `/api/quizzes` 401.
4. Observe the stale selector and working “Open quiz” link.

**Test gap:** there is no browser/JavaScript test for Apply -> refresh -> selected context.

## B2 - A later Foundry failure can persist queued tool history but lose the proposal

**Classification:** confirmed failure-path defect from the shared change tracker.
**Evidence:** [`AssistantTurnRunner.cs:267-325`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Assistant/AssistantTurnRunner.cs#L267-L325),
[`AiCreditService.cs:75-123`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/AiCreditService.cs#L75-L123),
[`AssistantTurnRunner.cs:328-355`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Assistant/AssistantTurnRunner.cs#L328-L355)

After a mutating tool returns `queued:true`, the runner tracks the model call and function
response but does not save them. The next model call's credit reservation uses the same
scoped `GlosifyContext` and calls `SaveChangesAsync`, thereby committing those messages.
If the subsequent Foundry request fails or is cancelled, the final message containing
`PendingChangesJson` is never constructed/saved.

The durable history can therefore say that a create was queued while there is no review
card and no recoverable proposal. A later request reconstructs a new
`AgentToolContext.PendingChanges` list, so it cannot Apply the lost in-memory object.

**Fault-injection reproduction**

1. Fake/redirect Foundry so call 2 returns `create_vocabulary_quiz`.
2. Let call 3's credit reservation succeed.
3. Throw or cancel during call 3's Foundry operation.
4. Query messages: the create call/result exist; no final Active message holds the change.

**Test gap:** existing fake-generative-AI runner tests do not exercise a real shared credit
service context failing after a mutating tool.

## B3 - Crash after the status claim can leave Applied with no quiz

**Classification:** high-confidence crash-consistency risk.
**Evidence:** [`AssistantChangeWorkflow.cs:30-48`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Assistant/AssistantChangeWorkflow.cs#L30-L48),
[`ChangeApplier.cs:48-55`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Assistant/ChangeApplier.cs#L48-L55)

The workflow commits Active -> Applied in one database transaction. Only afterward does
`ChangeApplier` begin the quiz transaction. The catch compensation correctly restores
Active for ordinary exceptions, but it cannot run after process termination, App Service
worker loss, or machine failure.

If interruption occurs in that window, the message remains Applied, no quiz exists, and
subsequent Apply calls return zero because the message is no longer Active.

**Verification approach:** place a test-only process/fail-fast boundary after the first
save, restart against a relational database, then retry Apply. Normal unit-test exceptions
are insufficient because the catch compensates them.

## B4 - A stale or foreign collection can consume the proposal as Applied (0)

**Classification:** confirmed code-path defect.
**Evidence:** [`QuizService.cs:56-68`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Quizzes/QuizService.cs#L56-L68),
[`QuizDomainExceptions.cs:9-10`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Quizzes/QuizDomainExceptions.cs#L9-L10),
[`ChangeApplier.cs:470-516`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Assistant/ChangeApplier.cs#L470-L516)

A proposed quiz can name a collection that was valid when listed but is deleted, moved to
another language, or no longer owned before Apply. `QuizService` throws
`QuizCollectionNotFoundException`, which derives from `InvalidOperationException`.
`ApplyCreateQuizAsync` catches every `InvalidOperationException`, logs it and returns null.
No exception reaches `AssistantChangeWorkflow`, so compensation does not restore Active.
The transaction commits normally with `applied=0`.

The browser treats the 200 as success, displays “Applied (0)”, then takes its generic
“Changes applied. Reloading...” path. The proposal can no longer be retried.

**Regression test needed:** create a proposal to collection C, delete C before Apply, and
assert a retryable/error result rather than Applied status.

## B5 - Auto cannot use the authored-agent model default

**Classification:** behavior/configuration mismatch; current production happens to use the
same deployment, so there is no present output difference.
**Evidence:** [`assistant.js:795-805`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/wwwroot/js/assistant.js#L795-L805),
[`GenerativeAiOptions.cs:352-367`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Generation/GenerativeAiOptions.cs#L352-L367),
[`FoundryGenerativeAiClient.cs:276-308`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Generation/FoundryGenerativeAiClient.cs#L276-L308)

The browser sends null for Auto. The runner immediately resolves null to the application's
default and passes a nonblank model to the Foundry client. The client's comment/logic says
an authored model acts as a default only when no requested model is supplied, but the real
runner path never supplies null. A direct client test can reach that authored-default arm;
the normal browser path cannot.

## B6 - Concurrent Sends can collide on message sequence

**Classification:** high-confidence concurrency risk.
**Evidence:** [`AssistantTurnRunner.cs:154-171`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Assistant/AssistantTurnRunner.cs#L154-L171),
[`AssistantMessageConfiguration.cs:11-15`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Data/Configurations/AssistantMessageConfiguration.cs#L11-L15)

Two requests for T can both load the same message list, calculate the same
`nextSequence`, and attempt to insert that sequence. The unique index correctly prevents
silent duplicates, but there is no per-thread serialization or retry. The local page
disables its Send button; two tabs, clients, or replayed requests can still race. One
request is expected to receive a database/update failure rather than a normal assistant
response.

**Test gap:** no relational concurrent-Send test was found.

## B7 - Context persistence is fire-and-forget

**Classification:** high-confidence client race.
**Evidence:** [`assistant.js:165-176`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/wwwroot/js/assistant.js#L165-L176),
[`assistant.js:178-222`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/wwwroot/js/assistant.js#L178-L222)

`persistContext` calls `updateChat(...).catch(...)` but returns nothing and is not awaited
by `setQuizContext`/`setMaterialContext`. Each PATCH carries a complete snapshot. For a
concrete race, select material A (PATCH 1), immediately select material B (PATCH 2), and let
PATCH 2 finish first: the older PATCH 1 can finish last and restore stale material A in
SQL. A Send made between them carries the current browser values and updates the thread,
but an older outstanding PATCH can still overwrite that stored context afterward. Other
tabs and later context fallbacks then see stale, coalesced state.

The confirmed B1 path currently prevents the post-create call entirely, but the race still
exists for normal selector changes.

## B8 - Authored tools replace profile tools without a runtime profile allowlist

**Classification:** configuration-boundary risk, not an exploit proven in the dated live
definition.
**Evidence:** [`FoundryGenerativeAiClient.cs:196-209`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Generation/FoundryGenerativeAiClient.cs#L196-L209),
[`AssistantToolRegistry.cs:17-63`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Assistant/AssistantToolRegistry.cs#L17-L63)

When the authored agent has tools, those declarations replace the selected profile's
code-side list. Local dispatch then resolves any name present in the registry; it does not
re-check membership in the current profile. A portal edit creates a new immutable version;
if configuration later selects that version, it could offer a registered tool outside the
intended Librarian surface. Tool handlers still enforce their own ownership/validation,
but profile separation is not a runtime authorization boundary.

The live v3 definition exactly matched the checked-in 23-tool export on 2026-08-11, so no
current mismatch was found. Saved prompt-agent v3 is immutable; it cannot silently drift in
place. The gap is lifecycle/configuration control: CI does not publish and parity-check a
new version, enforce its profile surface, update the app's selected version, or guard the
pinned version's availability.

## B9 - An ambiguous transaction commit could duplicate creation

**Classification:** low-frequency, high-confidence platform failure mode; fault injection
needed.
**Evidence:** [`ChangeApplier.cs:48-56`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Ai/Assistant/ChangeApplier.cs#L48-L56),
[`QuizService.cs:71-85`](https://github.com/gusbo9233/Glosify-.net/blob/1c817ee66c7673b1dc90bfbe397bb188d59539ed/Glosify/Services/Quizzes/QuizService.cs#L71-L85)

The execution strategy retries the entire delegate. If connectivity fails while SQL is
committing, the client can be unable to tell whether the commit succeeded. A retry creates
a fresh random Quiz GUID, and the operation has no stable idempotency key/verification
delegate. This can theoretically leave two equivalent quizzes if the first commit actually
succeeded.

Microsoft documents commit ambiguity and recommends idempotency/verification strategies in
[EF Core connection resiliency](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency).
This report does not claim it occurred in production.

## B10 - App Service platform Health Check is not configured

**Classification:** confirmed live operations issue.
**Evidence:** read-only Azure snapshot, 2026-08-11.

The application exposes working `/healthz` and `/readyz` endpoints, but the App Service
`healthCheckPath` was empty. Azure's platform Health Check therefore does not probe or use
the application's liveness endpoint for worker health decisions. Configure `/healthz` for
platform Health Check; `/readyz` includes dependency readiness and can remove a sound
worker during a transient SQL outage.

See [App Service Health Check](https://learn.microsoft.com/en-us/azure/app-service/monitor-instances-health-check).

## B11 - Low absolute tier capacity; current headroom is not measured

**Classification:** reliability/capacity posture, not a bug in the traced request.

The snapshot showed **one App Service B1 worker** and non-zone-redundant Basic DTU SQL
(5 DTUs, 2 GB maximum). “One” applies to the App Service worker count; this report does not
claim Azure SQL is one exposed physical server. Both tiers have low absolute capacity and
no zone-redundant posture. A worker recycle loses the authored-agent cache, and CPU, memory,
DTU, connection, or storage pressure can increase end-to-end latency. No utilization or
headroom time series was captured, so this is a posture finding rather than proof of
current saturation.

## B12 - Network and local-auth posture is broader than least privilege

**Classification:** security hardening opportunity; authentication remains enforced.

The dated snapshot showed:

- SQL public network access, `AllowAllWindowsAzureIps`, and three additional public-IP
  firewall rules;
- no SQL private endpoint;
- App Service main and SCM access restrictions both defaulted to Allow; the learner site
  is intentionally public, while deployment/SCM can be governed separately;
- Foundry and Application Insights/Log Analytics ingestion/query endpoints were publicly
  reachable at the network layer;
- Foundry network default Allow and local/key authentication still enabled even though the
  app uses managed identity;
- application telemetry used an Application Insights connection string/instrumentation key,
  not a managed-identity credential. Log Analytics query access still requires Entra/RBAC.

This does not mean anonymous data access. It means more networks/credential types can reach
the authentication boundary than a private-network/managed-identity-only design would
allow. Enabled Foundry local auth does not prove that any key was used or distributed.

Hardening must preserve the GitHub Actions deployment path. Restrict SCM independently
where compatible with that deployment identity/network, add App Service VNet integration
and private DNS before disabling public SQL/Foundry access, and verify name resolution and
egress first. Entra-only Application Insights ingestion would require adding an Azure
credential, configuration, and an appropriate role before disabling local authentication;
the current code does not do that.

See [Azure SQL network access controls](https://learn.microsoft.com/en-us/azure/azure-sql/database/network-access-controls-overview?view=azuresql),
[App Service access restrictions](https://learn.microsoft.com/en-us/azure/app-service/overview-access-restrictions),
[Foundry private link](https://learn.microsoft.com/en-us/azure/foundry/how-to/configure-private-link),
and [Microsoft Entra authentication for Application Insights](https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication).

## B13 - Repository hosting/database descriptions are stale

**Classification:** confirmed documentation/comment defect.

`Program.cs` and tracked deployment text describe SQL serverless cold start/auto-pause,
while live `glosifydb` is Basic DTU with `autoPauseDelay=null`. An untracked architecture
document described Windows hosting, while the live App Service is Linux B1. These
statements can mislead troubleshooting and learning even though they do not change runtime
behavior.

## B14 - Model auto-upgrade weakens run reproducibility

**Classification:** configuration-boundary reproducibility risk.
**Creation result:** no failure was observed in the dated fixture.

The Foundry deployment `gpt-5.6-luna` reported model version `2026-07-09` and
`versionUpgradeOption=OnceNewDefaultVersionAvailable`. Prompt-agent v3 and the application
commit can remain unchanged while Azure moves the deployment to a new default model
version. Model behavior, tool selection, token use, latency, and safety behavior can
therefore change independently of the pinned prompt-agent version.

Choose deliberately between automatic patching and exact reproducibility, record the
effective model version in operational evidence, and test the assistant fixture before or
immediately after an upgrade. Microsoft documents the tradeoff in
[Foundry model versions and upgrades](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/model-versions).

## B15 - App Service resource logs are not routed

**Classification:** confirmed live observability gap.
**Evidence:** read-only Azure snapshot, 2026-08-11.

The application exports code-based OpenTelemetry through Application Insights, but the
App Service resource had no diagnostic setting. Application spans/logs do not substitute
for platform HTTP, console, audit, or other resource-log categories. Platform host
monitoring still exists in Azure, but those resource logs were not configured for durable
Log Analytics routing in the inspected setup.

Enable only the operationally useful diagnostic categories, choose retention/cost
deliberately, and verify ingestion with a dated query. This does not require changing the
successful assistant workflow. See
[Azure Monitor diagnostic settings](https://learn.microsoft.com/en-us/azure/azure-monitor/platform/diagnostic-settings).

## Verification and next tests

On 2026-08-11, the focused assistant, tool, Foundry, credit and relational-Apply filter
completed with **142 passed, 0 failed and 0 skipped** on .NET 10. The document/link audit
also verified every fixed-commit source link and line range in this report. No application
code was changed, so these results establish the pre-existing baseline rather than proving
that the possible defects below have been fixed.

Missing high-value regression coverage:

1. browser Send -> proposal -> Apply -> quiz selector/link;
2. Foundry failure after a mutating tool and after the next credit save;
3. stale collection between proposal and Apply;
4. concurrent Sends to one thread;
5. process-loss recovery after the Applied claim;
6. ambiguous-commit fault injection/idempotency;
7. authored tool parity/profile enforcement against the live/exported agent;
8. a canary across model-version upgrades;
9. App Service diagnostic-setting ingestion verification.

No application fixes were made in this task.

## Resolution notes (2026-08-11)

These notes record later remediation without rewriting the dated evidence above.

| IDs | Resolution |
|---|---|
| B1 | Fixed. Apply now returns an optional created-quiz summary; the browser appends and selects it directly, persists the context, and makes no cookie-authenticated `/api/quizzes` request. |
| B2 | Fixed. Intermediate model/function rows remain detached until the final assistant message and `PendingChangesJson` are saved together. |
| B3, B9 | Fixed. The workflow owns one relational execution-strategy transaction for quiz changes and Active-to-Applied status, with Applied status as the commit-verification token. |
| B4 | Fixed. Typed stale/deleted collection and collection-conflict failures propagate to the existing Problem Details mapper; the proposal remains Active and the client restores Apply/Reject controls. |
| B6 | Fixed. A nullable, database-backed per-thread turn lease is acquired before persistence/credit use, renewed before every model invocation, and ownership-checked on release. Concurrent Send returns stable `409 conflict`. |
| B7 | Fixed. Complete browser context snapshots are queued in invocation order, the queue recovers after a failed PATCH, and post-Apply persistence is awaited. Cross-tab behavior remains last-writer-wins. |
| B10, B15 | Operator remediation prepared. `scripts/configure-production-observability.sh` idempotently configures `/healthz` and resource-specific App Service operational logs. Running it against production still requires separate production-change approval and post-run ingestion verification. |
| B13 | Fixed. Runtime comments, the deployment runbook, and architecture documentation now describe Linux B1, Basic 5-DTU SQL without auto-pause, code-based OpenTelemetry, and the health/diagnostic paths. |
| B5, B8, B11, B12, B14 | Deferred by scope: model-default policy, authored-tool policy, capacity changes, private networking/local-auth removal, and model-version pinning remain separate decisions. |

Regression coverage now includes incomplete-turn history persistence, relational Apply
rollback/success/retry behavior, stale collection propagation, lease ownership/renewal/
expiry takeover, stable conflict Problem Details, and ordered/recoverable client context
writes. The dated baseline and source links above intentionally remain unchanged.
