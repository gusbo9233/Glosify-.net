# Glosify Live Subtitles extension

The source directory is intentionally not a loadable unpacked extension. Build one of the explicit profiles so development permissions cannot leak into the Store package.

- `npm run build:dev` produces `artifacts/development` with `https://localhost/*` and `http://localhost/*` permissions and connects to `https://localhost:7032`.
- `npm run build:store` produces `artifacts/store` with only `https://glosify.se/*`.
- `npm run package:store` rebuilds, validates, and creates `artifacts/package/glosify-live-subtitles-0.4.0-beta.zip` with `manifest.json` at its root.

The Store validator rejects unexpected files, localhost references, missing or incorrectly sized icons, likely embedded secrets, and common remote/dynamic-code patterns.

The server catalog supplies the available top-level subtitle modes and the
current Azure Translator target-language list. Scribe uses ElevenLabs Scribe v2
plus Azure Translator, and Enhanced uses Microsoft Foundry. Spoken-language
detection defaults to automatic; the optional hint selector appears whenever
Scribe will process audio, including Enhanced transcript saving. Scribe remains
absent until its server-side key and monthly-budget price are configured.
