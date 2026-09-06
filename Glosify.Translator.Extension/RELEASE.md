# Translator release checklist

The ZIP is a draft-upload candidate, not proof that production is ready. Chrome
assigns the Store ID when the first draft is uploaded, before publication.
The development ID and its localhost login session are deliberately separate.

## 1. Verify and create the draft package

Use Node 24 and the repository's locked dependencies:

```sh
npm ci --prefix Glosify.LiveSubtitles.Extension
npm ci --prefix Glosify.Translator.Extension
cd Glosify.Translator.Extension
npm test
npm run lint
npm run test:browser
npm audit
npm run package:store
```

Upload `artifacts/package/glosify-translator-0.1.0-beta.zip` as a new draft in
the Chrome Web Store developer dashboard. Do not submit for review yet. Never
upload `artifacts/development`, `artifacts/test`, a browser profile, credentials,
or a private signing key. The Store package has no pinned development key.
All runtime code is bundled; the extension's only API host is `https://glosify.se`.

Copy the assigned 32-letter ID from the draft and run:

```sh
npm run check:production -- <assigned-store-id>
```

This read-only command prints the required callback and checks anonymous API
responses and the public privacy page. It does not sign in, call AI, change
server settings, inspect secrets, or prove that the callback is allowlisted.
It also runs without an ID to check the public deployment before the upload.

## 2. Deploy and configure the backend

- Deploy the reviewed Translator backend, library UI and updated English privacy
  policy through the existing application release process. The application does
  not apply database migrations at startup; apply the reviewed Translator schema
  migrations using the existing controlled database release procedure.
- Keep existing Live Subtitles callback entries. Append this exact value to the
  production `ExtensionAuth:AllowedRedirectUris` array:
  `https://<assigned-store-id>.chromiumapp.org/glosify`.
  For environment/App Service configuration the corresponding key is
  `ExtensionAuth__AllowedRedirectUris__N`, where `N` is the next unused array index
  in the existing effective configuration. Do not replace an existing entry.
  Do not insert a wildcard, placeholder, localhost URL or development ID.
- Confirm production has its existing OpenAI credential, paid-services/credit
  configuration and legal controller/contact settings. Never put provider keys,
  passwords or tokens into the extension or Store listing.
- Follow ADR 0001: this deployment supports one application instance, not
  uncoordinated scale-out. Plan restarts around active authorization flows.
- Restart/redeploy through the normal release process after configuration changes.
- Rerun `check:production`. Both `/api/me` and `/api/translator/catalog` must
  return HTTP 401 without credentials, not a redirect to login or an HTML page.
  `/privacy/english` must be publicly accessible and disclose Translator/OpenAI.

Observed on 6 September 2026: production `/api/me` returned 401, but the
Translator catalog returned 302 to `/login`; the live English privacy policy
described only Live Subtitles. These are release blockers until corrected.
No production deployment or database change was made while preparing this package.

## 3. Exercise the assigned Store identity

Use the assigned identity, not the DEV build. To test before distribution,
Chrome documents copying the draft's public key from **Package → View public
key** into the `key` field of a separate, unpacked copy of the Store build.
Keep that preview outside `artifacts/store` and do not repackage it. Verify its
ID in `chrome://extensions` matches the draft exactly. See
[Chrome's identity guidance](https://developer.chrome.com/docs/extensions/reference/manifest/key).

- With a production test account, choose Connect and finish Glosify sign-in.
  Confirm the email and credit balance match that same account on `glosify.se`.
- Repeat with an active production web session, and after closing/cancelling the
  authorization window. Reconnect must remain possible. Localhost sessions and
  credits are not production sessions or credits.
- Translate a harmless short sentence; confirm one request/credit charge and the
  returned balance. Save explicitly and find it in `/Translations`. Delete the
  test item using the library UI. No automatic save should occur.
- Verify insufficient credits, sign-out, reconnect, and browser restart. An
  unavailable balance must not appear as zero. Do not grant fake test hooks to
  the Store build or bypass paid-service checks for reviewers.
- On Gmail and another shortcut-heavy page, type letters and spaces in both
  fields, paste, use IME if relevant, copy, move, resize, minimize and reopen.
  No underlying shortcut should fire while an input has focus. Chrome internal
  pages, the Web Store and other protected tabs cannot accept injected UI.
- Confirm text/preferences/results are not emitted into the containing page,
  developer logs or analytics. The disclosed local preference text remains until
  edited/cleared or the extension is removed; sign-out removes its login token.

## 4. Complete Store review materials

Use `store-listing/CHROME-WEB-STORE.md` for listing copy, single purpose,
permission explanations, data-use declarations and reviewer instructions.
Verify operator/contact and provider-retention statements against the actual
production service. The developer remains responsible for these declarations.

Set privacy URL to `https://glosify.se/privacy/english` and support URL to
`https://glosify.se/support/english`. Capture current UI screenshots for the
listing (the existing assets predate the new in-product disclosure); never
include private user text or credentials. Provide a dedicated review account
with sufficient credits through the dashboard's private test instructions.
Enable the developer account's required two-step verification and resolve all
dashboard validation warnings before submission.

Chrome decides approval. Local checks cannot certify Web Store compliance or
replace a real production identity/Translate/Save test.

## Local verification on 6 September 2026

- `npm test`: 46 passed; `npm run test:browser`: 7 passed against isolated mock APIs.
- `npm run lint`: passed; `npm audit`: 0 known vulnerabilities.
- Two `npm run package:store` builds produced identical SHA-256 checksums.
- From the repository root:

  ```sh
  dotnet test Glosify.Tests/Glosify.Tests.csproj --no-restore --filter 'FullyQualifiedName~TextTranslationServiceTests|FullyQualifiedName~ExtensionAuthorizationCodeStoreTests|FullyQualifiedName~AccountReturnUrlTests|FullyQualifiedName~NavigationTests|FullyQualifiedName~PaidServiceCoverageTests'
  ```

  Passed: 72, Failed: 0, Skipped: 0. The build reported NU1900 because cached
  NuGet vulnerability metadata could not be fetched; this was not a successful
  fresh NuGet audit. The full backend suite was not run for this extension change.
- `npm run check:production`: failed on the two live deployment blockers above,
  as intended. No paid production request or real production login was tested.

Primary policy references:

- [Chrome Web Store policies](https://developer.chrome.com/docs/webstore/program-policies/policies)
- [User data and prominent disclosure](https://developer.chrome.com/docs/webstore/program-policies/user-data-faq)
- [Upload and publish an extension](https://developer.chrome.com/docs/webstore/publish)
