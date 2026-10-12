# Assistant disconnect investigation — 2026-09-27

## Confirmed production evidence

Read-only Application Insights queries against `appinsights-glosify` show these
recent `POST Assistant/Chats/{threadId:guid}/Send` requests on `www.glosify.se`:

| Start (Europe/Stockholm) | Duration | Result | Operation id |
| --- | --- | --- | --- |
| 2026-09-27 12:28:48 | 60.753 s | 499, cancelled | `ffd1136d66b9ff6e2fc0fd35c94d28c8` |
| 2026-09-27 12:29:48 | 18.953 s | 200, success | `8acb90a8d14ef6140a353703de19b7be` |
| 2026-09-27 12:44:24 | 60.332 s | 499, cancelled | `dcd57147caf44f1409ffe78cf37a626b` |
| 2026-09-27 12:56:27 | 60.193 s | 499, cancelled | `d08446f772af025c7c7ad3e62e99ea2c` |

Across the preceding seven days, this endpoint recorded seven successful sends
(5.332–55.486 seconds), six 499 sends (60.158–64.543 seconds), and one 409 send.
These are the recorded requests, not proof of complete telemetry coverage.

For all three cancelled requests above, dependency traces show successful OpenAI
Responses API calls and successful `create_vocabulary_quiz` executions before a
subsequent model invocation was cancelled. Their assistant spans report `cancelled`,
and exception records contain `TaskCanceledException`. The latest request completed
three quiz-creation tool executions before cancellation.

The web app was Running during inspection. Public response headers showed Kestrel
and Azure affinity cookies. Resource Health could not be verified: the health API
returned an authorization/provider-registration conflict. No production settings
were changed, and no chat contents or provider credentials were queried.

## Interpretation and remaining uncertainty

The repeatable cutoff strongly indicates a roughly one-minute downstream connection
timeout. The server sees the client side disconnect; the evidence does not identify
whether the browser, a browser's networking layer, or another intermediary closes
it. The exact screenshot-to-request correspondence is not established without its
time. There is no 60-second send timer in the checked-in assistant JavaScript.

This differs from the repository's 180-second **per-provider-call** timeout and
Azure App Service Linux's documented approximately 240-second idle request timeout:
[Microsoft guidance](https://learn.microsoft.com/en-us/troubleshoot/azure/app-service/web-request-times-out-app-service).

`AssistantController.ChatSend` awaits the entire orchestrator using the HTTP request
cancellation token. `OpenAiGenerativeAiClient` links that cancellation into each
provider call. `AssistantTurnRunner` buffers intermediate messages and proposals
until final completion. Its cancellation path records the cancelled turn but does
not save the unfinished proposal as an applicable message. Successful provider
calls already completed and billed are not undone by the later disconnect.

The client displays the generic “Network error talking to the assistant” for any
exception in its send block. This also covers unrelated parsing/rendering failures,
so the text alone is not a diagnosis. The observed server cancellations independently
establish a transport problem in these recorded incidents.

## Required architectural follow-up

Quiz batching addresses output size but leaves the long HTTP request in place.
For reliable long tasks:

1. Accept the request and return a durable job/turn id promptly.
2. Process it in a worker with its own service scope and explicit cancellation,
   rather than inheriting browser disconnect cancellation.
3. Persist draft content, model/tool history, and progress at recoverable boundaries.
4. Let the browser poll status or subscribe to progress and reconnect without
   submitting duplicate work.
5. Preserve ownership, per-chat exclusion, credit accounting, and explicit Apply;
   provide cancellation, bounded execution, and safe restart recovery.

Increasing the provider timeout, retrying the POST automatically, or changing the
error label does not fix the confirmed loss of work. A browser/network trace near
the one-minute boundary is still needed to attribute the timeout to a component.

## Resolution

The assistant now runs as durable background runs with the properties listed
above; see [assistant-runtime.md](assistant-runtime.md). The request-reply
endpoints wait for a run rather than doing the work, so a dropped connection no
longer loses it.
