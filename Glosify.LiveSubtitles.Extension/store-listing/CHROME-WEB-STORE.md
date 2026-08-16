# Chrome Web Store submission — 0.4.0 BETA

## Publishing settings

- Visibility: **Unlisted**
- Publishing: **Deferred publishing**
- Pilot period before considering public distribution: at least seven days
- Support URL: `https://glosify.se/Home/Support`
- Privacy URL: `https://glosify.se/Home/Privacy`
- Terms URL: `https://glosify.se/Home/Terms`

## Listing copy

**Name**

Glosify Live Subtitles BETA

**Summary**

Translate audio from the Chrome tab you choose into live subtitles using your Glosify account.

**Detailed description**

Glosify Live Subtitles adds real-time translated captions to the Chrome tab you explicitly choose. Connect an existing Glosify account, choose ElevenLabs Scribe v2 or Enhanced subtitles, optionally select a spoken-language hint, choose the subtitle language, review the per-minute credit price, and press Start subtitles. Automatic spoken-language detection is the default. The extension captures only that tab's audio, keeps the audio playing locally, and places the translated caption overlay over the page.

In Enhanced mode, tab audio is streamed through Glosify to Microsoft Foundry. In ElevenLabs Scribe v2 mode, it is streamed through Glosify to ElevenLabs and finalized phrases are sent to Azure Translator. If transcript saving is enabled in Enhanced mode, the same audio is also sent to Scribe for the finalized source transcript. Standard ElevenLabs API logging is enabled and ElevenLabs may retain service-log data under its policy. Tab audio is not stored by Glosify. Transcript saving is separate, off by default, and stores only finalized original-language speech in the user's private Glosify account until the user deletes the transcript or account.

Provider-reported audio usage consumes the displayed credits even if captions are interrupted, inaccurate, or unusable; mandatory consumer rights still apply. AI-generated captions and translations may be incorrect and must not be relied on for safety-critical or other high-stakes decisions. Privacy, Terms, and Support links are available directly in the extension popup.

This is an unlisted BETA pilot. Paid features may close for the rest of the Europe/Stockholm month when Glosify's application budget is reached. The extension displays the reset time and stops at a paid-minute boundary.

Eligible Google- or Microsoft-linked accounts receive one 25-credit trial. Password-only registrations do not receive automatic trial credits.

## Single-purpose declaration

The extension's single purpose is to capture audio from a user-selected Chrome tab and show a real-time translated subtitle overlay for that tab through the user's Glosify account.

## Permission justifications

- `activeTab`: identifies and operates only on the tab where the user presses Start subtitles.
- `tabCapture`: captures audio from that explicitly selected tab for live subtitle processing.
- `scripting`: injects the local subtitle overlay script into the selected page. No remote code is downloaded or executed.
- `offscreen`: keeps the user-selected tab audio pipeline running and audible while the popup is closed.
- `storage`: stores the refresh token and subtitle-language preference in extension-local trusted contexts. Transcript consent resets after every session.
- `identity`: opens Chrome's extension authentication redirect for the Glosify PKCE connection flow.
- `https://glosify.se/*`: connects to Glosify for authentication, credits, service status, realtime session authorization, and optional transcript management. The Store build has no localhost or broad-host permission.

## Privacy questionnaire answers

**Personally identifiable information:** Yes. The extension handles the signed-in account email and Glosify authentication tokens.

**Authentication information:** Yes. A Glosify refresh token is stored in `chrome.storage.local` with access restricted to trusted extension contexts. Access tokens are held in memory and refreshed as needed.

**User activity / website content:** Yes, narrowly. After an explicit Start action, audio from the selected tab and translated caption text are processed to provide subtitles. Browsing history and unrelated tabs are not collected.

**User-generated content:** Optional. If the separate, default-off transcript switch is enabled, finalized original-language speech is stored in the user's private Glosify account until deletion.

**Financial/payment information:** No card, bank, or payment credentials are collected. The extension reads Glosify service-credit balances and per-minute credit prices.

**Data sale / advertising / unrelated use:** No. Data is not sold, used for personalised advertising, or transferred for lending or unrelated purposes.

**Processors:** Microsoft Azure and Microsoft Foundry provide hosting, authentication infrastructure, storage, monitoring, and realtime translation processing. ElevenLabs provides optional Scribe v2 speech processing in Scribe subtitle mode and when source-transcript saving is enabled for Enhanced mode. Google or Microsoft processes authentication data when the user chooses that sign-in provider.

**Limited Use:** Glosify's use and transfer of information received from Google APIs adheres to the Chrome Web Store User Data Policy, including Limited Use requirements.

## Reviewer instructions

1. Install the uploaded package and pin the extension.
2. Open a regular HTTPS page that is playing speech audio, such as a video page.
3. Open the extension and choose **Connect Glosify**. The browser is directed to Glosify's existing `/extension/connect` PKCE login flow.
4. Sign in with the temporary password reviewer account entered only in the Chrome Web Store dashboard. That account is manually credited; its credentials must never be added to this repository or listing text.
5. Select a subtitle mode, optionally choose a spoken-language hint (Auto detect is the default), and choose the quiz and subtitle languages. Confirm transcript saving is unchecked.
6. Read the mode-specific provider and credit disclosure and press **Start subtitles**. Captions appear in an overlay on the page.
7. Stop the session. Optionally enable transcript saving for a second session, then use **View saved transcripts** to verify and delete it.
8. Budget-exhaustion behavior can be reviewed using the supplied test account/environment instructions in the dashboard: blocked paid calls return HTTP 503 with code `paid_services_budget_exhausted` and a reset timestamp, while login, legal pages, reads, deletion, health, and admin diagnostics remain available.

## Assets

- Icon PNGs: `icons/icon16.png`, `icons/icon32.png`, `icons/icon48.png`, `icons/icon128.png`
- Small promotional tile: `store-listing/assets/promo-440x280.png`
- Screenshots: `store-listing/assets/screenshot-live-1280x800.png`, `store-listing/assets/screenshot-settings-1280x800.png`

The screenshots are deterministic frames around the actual extension screenshots in `docs/screenshots`; they do not depict features that are absent from the product. The settings screenshot uses the obvious reviewer placeholder instead of the operator's personal email.

## Manual dashboard checklist

- Upload only `artifacts/package/glosify-live-subtitles-0.4.0-beta.zip`.
- Enter the temporary reviewer credentials only in the dashboard reviewer field.
- Confirm data-use declarations match the answers above.
- Confirm unlisted visibility and deferred publishing before submitting.
- Do not publish until `Legal__ControllerName` and `Legal__ContactEmail` are configured in production and all three public pages return 200.
