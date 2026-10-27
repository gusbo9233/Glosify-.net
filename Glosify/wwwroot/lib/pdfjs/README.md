# Vendored PDF.js

These files are from `pdfjs-dist` **4.10.38**, build `f9bea397f`.
Upstream package: https://registry.npmjs.org/pdfjs-dist/-/pdfjs-dist-4.10.38.tgz

The worker is unchanged. The display bundle has one local fix, applied to both
`PDFFetchStreamReader.cancel` and `PDFFetchStreamRangeReader.cancel`:

```diff
- this._reader?.cancel(reason);
+ this._reader?.cancel(reason).catch(() => {});
  this._abortController.abort();
```

Cancelling an errored Fetch response body rejects with the existing stream error.
These methods perform best-effort teardown; handling that rejection prevents an
unhandled promise rejection without changing read errors or aborting behavior.
The same issue was still present in upstream `master` on 2026-10-27.

`Glosify.ClientTests/pdfjs-cancellation.test.js` executes the shipped reader classes
against open, closed, errored and not-yet-started streams. Preserve this coverage
when replacing the bundle; remove the local patch once upstream handles rejection.

Original SHA-256 hashes:

- `pdf.min.mjs`: `27fc2a057a00f92a4334ad06e17dbd7259912954e9fb7f76400bcca5fd190a9c`
- `pdf.worker.min.mjs`: `1baa1844c89c80a5b2797c916e75ab29254be46d8e9cb53cb6364d7aad84be36`
