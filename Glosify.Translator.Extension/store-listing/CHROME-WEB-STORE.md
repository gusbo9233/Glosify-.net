# Glosify Translator BETA

Translate typed or pasted text in a focused, movable workspace on any page. Choose Auto-detect or a source language, select a target language, and optionally request a dialect, tone, register, or wording style. Copy results immediately or save selected translations privately to your Glosify account, grouped by translator session.

The extension accesses only the active tab after you click Start. It stores your Glosify refresh token, selected languages, and translation preferences in trusted extension storage. It does not store translation history in Chrome and never saves a translation automatically.

Permissions: `activeTab` and `scripting` create the translator after a user action; `identity` signs in through Glosify PKCE; `storage` remembers trusted settings; `clipboardWrite` copies an explicit result. Production host access is limited to `https://glosify.se/*`.
