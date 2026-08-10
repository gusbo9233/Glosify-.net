# Glosify Live Subtitles extension

The source directory is intentionally not a loadable unpacked extension. Build one of the explicit profiles so development permissions cannot leak into the Store package.

- `npm run build:dev` produces `artifacts/development` with `https://localhost/*` and `http://localhost/*` permissions and connects to `https://localhost:7032`.
- `npm run build:store` produces `artifacts/store` with only `https://glosify.se/*`.
- `npm run package:store` rebuilds, validates, and creates `artifacts/package/glosify-live-subtitles-0.4.0-beta.zip` with `manifest.json` at its root.

The Store validator rejects unexpected files, localhost references, missing or incorrectly sized icons, likely embedded secrets, and common remote/dynamic-code patterns.
