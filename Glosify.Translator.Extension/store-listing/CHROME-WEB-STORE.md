# Glosify Translator BETA

## Public description

Translate text you type or paste in a movable workspace on regular web pages.
Choose a source language or Auto-detect, choose a target language, and optionally
request a dialect, tone, register or wording style. Copy the result or explicitly
save it privately to your Glosify library, grouped by translator session.

A Glosify account and sufficient Glosify credits are required for translation.
Translation uses credits; installing the extension does not include unlimited
translation. AI translations can be inaccurate—review important results.

Click Start translator to open the workspace on the active tab. The extension
does not automatically read page text, email, browsing history or clipboard
contents. It cannot run on Chrome internal pages, the Chrome Web Store or other
browser-protected pages.

When you choose Translate, your entered text and optional preferences are sent
over HTTPS to Glosify and processed by OpenAI. Results are added to your Glosify
library only when you choose Save. Selected languages, preferences and a sign-in
token are stored locally in trusted extension storage; translation history is
not stored in Chrome. Account email and balance are used to display your account.
Glosify does not sell extension data or use it for advertising.

Privacy: https://glosify.se/privacy/english
Support: https://glosify.se/support/english

## Dashboard: single purpose

Translate user-entered text through the user's Glosify account, with optional
explicit saving of those translations to the same account's language library.

## Dashboard: permission justifications

| Permission | Why the current feature needs it |
| --- | --- |
| `activeTab` | Temporarily access the tab the user chooses after pressing Start translator. No automatic page reading or persistent all-sites content script. |
| `scripting` | Insert the translator's frame host in that active tab after the user's action. |
| `identity` | Use Chrome's authorization window and extension-specific callback to connect a Glosify account with PKCE. |
| `storage` | Remember the refresh token, selected languages and optional preferences in local trusted extension storage. |
| `clipboardWrite` | Copy the translated result only when the user clicks Copy. No clipboard reading permission is requested. |
| `https://glosify.se/*` | Exchange/refresh authentication and call Glosify's account, language catalog, translation and explicit-save APIs over HTTPS. |

The web-accessible `overlay/translator.html` lets the user-invoked workspace
appear on HTTP/HTTPS pages. It is not a persistent host permission. Its controls
run in the extension origin; the host page receives geometry messages only.

## Dashboard: data handling declarations

Do not declare “no user data.” Review the dashboard's current category descriptions
and disclose at least the handling of:

- Personally identifiable information: account email/identifier displayed or used
  by the account service.
- Authentication information: short-lived bearer credentials and the locally
  stored refresh token. Password entry happens on Glosify, not in the extension.
- Website/user-provided content: source text, translated text, languages and
  optional preferences submitted by the user. Content may include personal
  communications if users paste messages; declare this capability as applicable
  to the dashboard's definitions rather than claiming such content is impossible.
- Account credit balance/usage: the extension displays the balance, and server
  credit accounting is necessary for the paid translation feature. Check the
  dashboard's financial/payment category definition; it does not receive cards.

No automatic browsing-history collection, analytics SDK, ad tracking, audio,
location collection or remote executable code. Local processing/storage also
counts as data handling under Chrome policy. Glosify server operational/security
logs are covered by the linked privacy policy. Certify Limited Use statements
only after confirming actual production practices; declarations must match the
UI, policy and service, including OpenAI processing and optional local preferences.

## Private reviewer instructions (complete in dashboard, not this repository)

Provide a dedicated production test account with sufficient credits and any
required access instructions in the dashboard's private reviewer fields. Never
put its password, recovery codes or a provider API key in this file or package.

1. Open the extension, choose Connect Glosify and sign in with the review account.
2. Verify the displayed account email and credit balance.
3. Open a regular web page, then press Start translator in the extension popup.
4. Enter “Hello, how are you?”, choose Spanish, optionally enter “Informal tone”,
   and click Translate. The result should appear and the balance should update.
5. Click Copy; then click Save. Open Saved translations in the popup to find the
   item in the account's Glosify library. Saving is never automatic.
6. Type letters and spaces in both fields without triggering host-page shortcuts;
   move, resize, minimize and reopen the workspace. Sign out and reconnect.

Do not submit until the assigned Store callback is configured on production and
these steps work with the Store identity. Use RELEASE.md for that sequence.
