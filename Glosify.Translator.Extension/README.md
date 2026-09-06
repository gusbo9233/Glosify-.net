# Glosify Translator Chrome extension

An independent Manifest V3 extension for translating typed or pasted text through Glosify. It stores only the refresh token, selected languages, and preference text in Chrome's trusted extension storage. Translation history is never stored in Chrome; saving a completed result is explicit and writes to the signed-in user's Glosify library. Saves made while the same translator box remains open are grouped into one session; closing or reloading the page starts a new session.

The translator controls run in an extension-origin iframe, so the underlying page
cannot read input or receive its keyboard events (including Gmail shortcuts and
Space). The injected host handles only focus and geometry. Only the frame HTML is
web-accessible; API calls require a matching extension frame and an active host
created by Start. Text, preferences, results, and credentials never cross the
page's message channel. Reopening an existing box preserves its settings and result.

Temporary authentication-service failures retain the refresh token. Account
responses are tied to the current sign-in session so late responses cannot undo
sign-out or overwrite a different account. Network requests use Chrome's
[bounded long-operation keepalive](https://developer.chrome.com/docs/extensions/develop/migrate/to-service-workers#keep-sw-alive)
only until their response body completes, cancellation occurs, or the 210-second
timeout expires. Paid requests are not retried for transient failures.

## Development

1. Run Glosify at `https://localhost:7032`.
2. Run `npm install` and `npm run build:dev` in this directory.
3. Open `chrome://extensions`, enable Developer mode, and load `artifacts/development` unpacked.
4. The pinned development callback `https://gogoghheeaelbddlgiehjdpmnnpnpjfk.chromiumapp.org/glosify` is already present in the HTTPS launch profile's `ExtensionAuth:AllowedRedirectUris` list.

After rebuilding, reload the unpacked extension and refresh any tabs with an old
translator box so they use the updated host and extension frame.

The development profile has a pinned ID separate from Live Subtitles. The Store profile contains no key and grants host access only to `https://glosify.se/*`.

The permission set follows Chrome's documented temporary user-gesture model for
[`activeTab`](https://developer.chrome.com/docs/extensions/develop/concepts/activeTab)
and its supported non-Google OAuth flow through
[`identity.launchWebAuthFlow`](https://developer.chrome.com/docs/extensions/reference/api/identity).
Review the resulting installation copy against Chrome's
[permission-warning list](https://developer.chrome.com/docs/extensions/reference/permissions-list)
before each Store submission.

## Verification and packaging

```sh
npm test
npm run lint
npm run test:browser
npm run package:store
```

The deterministic package is written to `artifacts/package/glosify-translator-0.1.0-beta.zip`.
The build validates its exact file inventory, permissions, production-only API
configuration, disabled test hooks and packaged-code-only content security policy.
API calls never follow redirects or send website cookies. The popup identifies
the configured server and does not display an unknown credit balance as zero.

Follow [RELEASE.md](RELEASE.md) for draft upload, assigned Store ID, backend
deployment, callback configuration, production smoke tests and Store disclosures.
The callback is `https://<store-id>.chromiumapp.org/glosify`; it must be explicitly
allowlisted on production without removing existing Live Subtitles entries.

```sh
npm run check:production
# After the first draft upload:
npm run check:production -- <assigned-store-id>
```

These are read-only public endpoint checks, not end-to-end sign-in verification.
Publication and production deployment are not performed by the extension scripts.
