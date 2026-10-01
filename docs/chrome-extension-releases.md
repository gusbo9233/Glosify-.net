# Chrome extension CI/CD

Both extensions already run unit tests, ESLint, dependency audits, persistent
Chromium tests, Store package validation, and repeated-package checksum checks in
`.github/workflows/master_glosify.yml`. Pull requests and pushes to `master` run
these checks. The resulting ZIPs are saved as GitHub Actions artifacts.

`.github/workflows/chrome-web-store.yml` adds manual delivery to Chrome Web Store.
It uploads the exact ZIP from a successful `master` push run of the existing
workflow. It does not rebuild the extension, deploy the website, submit for
review, or publish. Releases for each extension are serialized. The run summary
records the source run, commit, manifest version, and ZIP SHA-256.

## One-time setup

1. Create each extension's item in the Chrome Web Store Developer Dashboard if
   it does not exist yet. Complete the listing, privacy disclosures, and initial
   draft upload there. The upload API updates existing items only.
2. Follow Google's [Chrome Web Store API setup](https://developer.chrome.com/docs/webstore/using-api):
   enable the Chrome Web Store API in a Google Cloud project, create an OAuth
   client, and authorize the publisher account with the
   `https://www.googleapis.com/auth/chromewebstore` scope. Obtain a refresh token
   using your own OAuth client credentials. Follow the guide's consent-screen
   instructions so the credentials are suitable for ongoing automation.
3. In GitHub repository **Settings → Environments**, create these environments:
   - `chrome-web-store-live-subtitles`
   - `chrome-web-store-translator`
4. Restrict each environment to the `master` branch and configure the following
   values. The IDs are configuration, while the OAuth values are secrets.

   | Kind | Name | Value |
   | --- | --- | --- |
   | Environment variable | `CWS_PUBLISHER_ID` | Publisher ID from the Store account settings |
   | Environment variable | `CWS_EXTENSION_ID` | This extension's 32-character Store item ID |
   | Environment secret | `CWS_CLIENT_ID` | OAuth client ID |
   | Environment secret | `CWS_CLIENT_SECRET` | OAuth client secret |
   | Environment secret | `CWS_REFRESH_TOKEN` | Publisher's OAuth refresh token |

   Both environments can use the same publisher credentials when the same
   account owns both items. Their extension IDs must be different. Enter secrets
   directly in GitHub; do not commit them or put them in extension configuration.
5. Merge the workflow and upload script into `master` to make the manual workflow
   available in Actions.

## Verify authentication without a browser

Run the workflow on `master` with `operation=check` and `extension=live-subtitles`.
It refreshes the access token from GitHub environment secrets and reads Store status.
It does not upload, publish, or cancel a submission, and needs no source run ID.

```sh
gh workflow run chrome-web-store.yml --ref master -f extension=live-subtitles -f operation=check
```

An OAuth app in Testing issues refresh tokens that expire after seven days. For
ongoing automation, move the Google Auth Platform audience to Production and
obtain a replacement refresh token after that change. Store the replacement in
`CWS_REFRESH_TOKEN`. This is a one-time setup; normal workflow runs refresh access
tokens automatically. The connection check reports a remaining token lifetime
when Google returns one. No token value is logged.

## Release an extension

1. Increase `version` in the extension's `manifest.base.json` above the version
   already uploaded to the Store. Update version-specific packaging references
   and release notes as needed. Currently Live Subtitles' package script and the
   main CI artifact/checksum paths contain explicit versions; keep those in sync.
2. Merge the change and wait for the **Build, migrate, and deploy Glosify** push
   run on `master` to succeed. This requirement includes the existing production
   deployment job. A failed, PR, or manually dispatched source run is rejected.
3. Copy the numeric run ID from its URL:
   `https://github.com/<owner>/<repo>/actions/runs/<run-id>`.
4. In Actions, select **Upload Chrome Web Store draft → Run workflow**, choose
   `master`, select `live-subtitles` or `translator`, choose `operation=upload`, and enter that run ID.
5. After the upload succeeds, open the Store dashboard, review the package and
   disclosures, and submit for review/publication. Google review still applies.

The workflow accepts existing versioned artifact names matching
`glosify-<extension>-*-beta` and requires exactly one ZIP. If artifacts have expired,
produce a new successful CI run. It never falls back to a local or untested build.

Before uploading, inspect Greptile and Copilot feedback for the exact source
revision and resolve valid findings. Record an unavailable review explicitly.
The workflow does not treat its connection check as approval to release.

Google rejects uploads while an item is in review. The workflow reports the
Store error and never cancels review automatically. Finish code review and all
fixes before submitting the Store draft for review.

If uploading fails or times out, inspect the Store dashboard before retrying;
the upload may have reached Google even when the network response was lost.
Upload processing is polled for up to 30 checks, ten seconds apart. Failed and
unknown states fail the job. OAuth response bodies and tokens are not logged.

For Translator's initial release, also follow
[its release checklist](../Glosify.Translator.Extension/RELEASE.md), especially
the production callback allowlist and sign-in verification. An extension upload
does not configure the backend.

## Local verification

```sh
node --test scripts/upload-chrome-web-store.test.mjs
```

These tests simulate token exchange, upload, polling, errors, and timeout without
contacting Google. A real authenticated upload must be verified after the GitHub
environment values are configured.

API references: [upload](https://developer.chrome.com/docs/webstore/api/reference/rest/v2/media/upload),
[upload states](https://developer.chrome.com/docs/webstore/api/reference/rest/v2/UploadState),
[status polling](https://developer.chrome.com/docs/webstore/api/reference/rest/v2/publishers.items/fetchStatus).
