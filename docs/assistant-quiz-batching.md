# Large quiz requests

The assistant builds a large quiz through repeated model/tool calls in the existing
assistant turn. The source stays in conversation history; the instructions ask the
model to divide it into sections, process every requested word and sentence, and
check coverage before finishing.

`create_vocabulary_quiz` (or Freestyle's `create_quiz`) starts a proposal and returns
a `draft_id`. Further calls with that id append to the same proposal. Each call
accepts up to 100 words/items and 100 sentences; the assembled quiz can exceed those
counts. Calls report cumulative counts and skipped entries. Metadata from the first
call fixes the name, languages, and collection. Draft ids only resolve within the
current turn, never against another user's saved proposals.

Repeated text is deduplicated across batches with case, whitespace, and trailing
sentence punctuation ignored. First occurrence and translation win. Text present
as both a word and a sentence is retained as a sentence.

`complete` defaults to false. The model sets it to true on the final batch or an
empty completion call after checking coverage. A batch with skipped entries cannot
mark the draft complete. While any draft is unfinished, a text-only model response
causes the runner to continue with draft ids and counts in its context. Apply still
uses the existing transaction and saves one quiz from the combined proposal.

Requests accept up to 50,000 characters. With `AssistantRuntime:Enabled`, the
[durable runtime](assistant-runtime.md) owns execution, checkpoints batches, and
supports interruption recovery. The web rollout adds progress polling, Stop,
Resume, steering, and approvals. Saved batches remain when a task pauses.

With the runtime disabled, the legacy request-time loop retains its 24-invocation
limit, cancellation, provider timeout, and credit checks. Reaching the limit
explicitly reports partial work; that legacy loop does not recover interrupted
requests. Coverage checks account for source spans but cannot prove semantic
translation or extraction accuracy.

Regression tests cover multi-batch assembly, duplicate batches, content-kind and
draft boundaries, premature final replies, invocation limits, and transactional
Apply of a quiz exceeding 100 words and sentences.
