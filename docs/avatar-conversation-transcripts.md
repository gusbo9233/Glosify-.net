# Avatar conversation context and transcripts

The avatar conversation window offers **Ask assistant**, **Make a quiz**, and
**Save transcript / Update transcript**. The assistant opens with the current
conversation as its reading material. Opening it pauses microphone capture;
use the conversation control to resume. The quiz action prefills a request for
review and submission through the normal assistant workflow.

**Automatically save conversations** is an account preference, off by default.
Enabling it saves the current exchange and future finalized turns. Manual Save
and Update remain available. Disabling automatic saving preserves saved text.
Saved conversations appear in the normal language-scoped transcript library,
with speaker labels, rename/delete controls, and assistant reading support.
Deleting a transcript suppresses automatic recreation for that conversation;
explicit Save, or re-enabling automatic saving, resumes it.

**Clear display** clears only the visible messages. Switching partner or practice
material, choosing **New conversation**, and following same-tab navigation with
unsaved turns offers Save / Discard / Cancel. A failed save or status check preserves
the source and cancels the transition. Cancel keeps the current voice session paused;
use the microphone control to resume. Leaving via refresh/close uses the browser's
unsaved-changes warning. Saved transcripts remain. Unexpected selection changes
(such as changing learning language in another tab) retain the old opaque reference;
returning to that selection can resume its context.

## Storage and boundaries

The server records finalized user speech and fully played, acknowledged avatar
replies. Partial speech, interrupted avatar replies, translations, suggestions,
and audio are excluded. This archive is independent of the avatar's rolling
24-turn model memory and the browser's 48-message display. Unsaved archives
expire after eight hours of inactivity and are bounded by the existing
128-memory cap and an aggregate 8,000,000-character cap. Only fully saved idle
archives may be reclaimed for capacity; new work is refused rather than evicting
unsaved turns. New voice turns stop before the 2,000-turn / 500,000-character
archive limits, leaving capacity for bounded utterances and replies. Server
restarts still discard unsaved archives; the settings explain this retention limit.

Assistant run input accepts an optional `avatarConversationId`. At acceptance,
the app validates access, ownership and language, then stores an immutable
snapshot in a separate, quota-accounted table. Runs hold only its ID and turn count;
unchanged versions are shared within a chat by content hash. Retries reuse their
original source. A new message can use the latest version already shared in the
same owned chat and language after live memory is forgotten, expires, or disappears
on restart. `read_avatar_conversation` returns bounded pages, scoped to the same
user, thread and language. Sending a
request therefore retains its shared source in the saved assistant chat even
without saving a transcript. The browser never supplies authoritative source
text. Opaque conversation-to-chat associations persist in tab session storage so
reopening Ask assistant after a reload returns to the same chat. Previously shared
material stays readable in its owned chat if access to the live avatar preview is
removed; new live captures still require that access. Conversation content is
untrusted learning material, not instructions.

The additive `AvatarConversationTranscripts` migration adds the account
preference, shared snapshot table and run references, transcript source kind and
conversation identity, and optional segment speaker. Subtitle defaults and
source/translation streams stay compatible. Avatar saves, preferences, and deletes
are serialized per owner within the documented single-instance deployment;
unrelated owners and subtitle deletes do not wait on that lock. Voice uses a bounded,
coalesced background save queue; replies and playback acknowledgements do not await
storage. Autosave off reports cached status without database access. SQL uniqueness
and resource accounting still apply. Shared snapshots are deleted with their assistant chat,
including resource-accounting entries. Existing account cleanup requires deleting
owned assistant chats before the identity row. Saved transcripts retain their
existing account cascade deletion. No deployment or production migration is
performed by this feature's local verification.
