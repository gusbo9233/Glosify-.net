# Glosify Live Subtitles extension

The source directory is intentionally not a loadable unpacked extension. Build one of the explicit profiles so development permissions cannot leak into the Store package.

- `npm run build:dev` produces `artifacts/development` with `https://localhost/*` and `http://localhost/*` permissions and connects to `https://localhost:7032`.
- `npm run build:store` produces `artifacts/store` with only `https://glosify.se/*`.
- `npm run build:store-local` produces `artifacts/store` with the Chrome Web Store public key and localhost permissions, connects to `https://localhost:7032`, and is intended only for unpacked local testing.
- `npm run build:test` produces the localhost-only profile used by persistent Chromium tests.
- `npm run test:browser` builds that profile and exercises the MV3 worker, offscreen audio, mock HTTP/WebSocket relay, concurrency, and navigation shutdown.
- `npm run package:store` rebuilds, validates, and creates `artifacts/package/glosify-live-subtitles-0.5.1-beta.zip` with `manifest.json` at its root.

Development and test builds pin a public key so their unpacked extension ID
stays stable. The Store build deliberately omits `key` so Chrome Web Store can
use the signing key associated with the existing dashboard item.
The Store-local build pins that public Store key so local authentication uses
the same extension ID as the published item without changing the Store package.

The Store validator rejects a pinned key, unexpected files, localhost references,
missing or incorrectly sized icons, likely embedded secrets, and common
remote/dynamic-code patterns.

The server catalog supplies the available top-level subtitle modes and current
target-language list. Scribe uses ElevenLabs Scribe v2 plus Cloudflare M2M100,
and Enhanced uses OpenAI's realtime translation API. Spoken-language
detection defaults to automatic; the optional hint selector appears whenever
Scribe will process audio, including Enhanced transcript saving. Scribe remains
absent until its server-side key and monthly-budget price are configured.
Scribe users can disable partial captions before starting a session. Final-only
Scribe sessions do not request interim speech results, avoiding interim
Cloudflare translation work. Enhanced keeps its normal streaming caption behavior.
