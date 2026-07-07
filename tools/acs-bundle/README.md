# ACS calling SDK bundle

`Glosify/wwwroot/lib/acs/acs-calling.min.js` is a committed build artifact: the
Azure Communication Services calling SDK bundled into one self-hosted file and
exposed as `window.acs`. It has to be self-hosted because the app's CSP is
`script-src 'self'`, and Microsoft does not publish a browser bundle of
`@azure/communication-calling`.

To regenerate (e.g. after bumping the SDK version in `package.json`):

```bash
cd tools/acs-bundle
npm install
npm run build
```

Then commit the updated `acs-calling.min.js`.
