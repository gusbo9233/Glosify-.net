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

The existing 8,000-character pasted-message limit, 24-invocation limit,
cancellation, provider timeout, and credit checks remain in effect. Reaching the
invocation limit explicitly reports partial work.
This is a bounded request-time loop, not a durable background job: interruption
recovery, cross-turn draft continuation, and live progress streaming are not provided.
Completion is the model's coverage assessment, not a deterministic proof that every
source word was included. A refusal before starting any draft is addressed through
the instructions, not forced mutation based on an intent guess.

Regression tests cover multi-batch assembly, duplicate batches, content-kind and
draft boundaries, premature final replies, invocation limits, and transactional
Apply of a quiz exceeding 100 words and sentences.
