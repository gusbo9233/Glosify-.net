# Assistant fixes on current master

The local `codex/assistant-review-fixes` branch starts at master
`e8a12e5c97d4e519bfdad2454d9cf0a9d6eb8f3f` and merges the reviewed assistant
work through `026f297`. Master already shipped much of that work in #119 and
#128; the resulting diff retains the remaining improvements rather than
replacing newer production features with an older snapshot.

Conflict resolution preserves master's Anki membership tools, learning history,
model/pricing configuration, branding, game/avatar migrations, bounded logical
source lines, selectable multiple answers, start-request idempotency and tool
arguments in synchronous/history responses. The new evaluation lease migration
follows master's avatar migration and its target model includes all master
entities. No applied migrations were removed or rewritten.

Undo compares each source-addition batch's study state before reversing the
batch's own synchronized-card journal. This reconciles the new safeguard with
master's individual Anki card journaling. Tests verify unchanged additions can
be removed while later direct inclusions or reviews preserve both the source
and cards. Existing skip-rating history remains supported by the service;
the current regular flashcard UI/controller still offers Again and Got it.
The refreshed quiz dialog aborts its old listeners before rebinding.

Focused verification: 329 .NET tests passed, 11 environment-dependent cases
skipped. All 241 client tests passed, including preserved start idempotency and
multiple-answer deselection. EF reports no pending model changes; the generated
idempotent migration SQL only adds evaluation lease columns and replaces the
corresponding index. The full Release solution run passed 1,421 tests with no failures; 42
environment-dependent .NET tests and all 41 application-browser tests were
skipped. The final Qodo review uses operation
`2331502e-28c5-4a78-a21d-b2ba72f264ab` against the exact master base above.

DeepSource has no result for this unpushed branch. Copilot/Greptile remain
unavailable under the repository's temporary credits policy. Nothing was
pushed, deployed or applied to a database.
