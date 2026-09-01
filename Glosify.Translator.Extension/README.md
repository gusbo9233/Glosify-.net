# Glosify Translator Chrome extension

An independent Manifest V3 extension for translating typed or pasted text through Glosify. It stores only the refresh token, selected languages, and preference text in Chrome's trusted extension storage. Translation history is never stored in Chrome; saving a completed result is explicit and writes to the signed-in user's Glosify library.

## Development

1. Run Glosify at `https://localhost:7032`.
2. Run `npm install` and `npm run build:dev` in this directory.
3. Open `chrome://extensions`, enable Developer mode, and load `artifacts/development` unpacked.
4. The pinned development callback `https://gogoghheeaelbddlgiehjdpmnnpnpjfk.chromiumapp.org/glosify` is already present in the HTTPS launch profile's `ExtensionAuth:AllowedRedirectUris` list.

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

The deterministic package is written to `artifacts/package/glosify-translator-0.1.0-beta.zip`. Before publication, add the assigned Chrome Web Store callback `https://<store-id>.chromiumapp.org/glosify` to production `ExtensionAuth:AllowedRedirectUris`. Store publication is intentionally out of scope.
