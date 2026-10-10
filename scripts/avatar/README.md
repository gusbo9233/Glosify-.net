# Avatar assets and verification

The admin-only `/Avatar` page is implemented inside the Glosify MVC application.
Enable `Avatar:Enabled` after applying `AddAvatarUsageOperations`. The existing
`Admin:UserIds` allowlist controls the page, HTTP API, and WebSocket. No shared game
authentication or separate game deployment is required.

## Providers and prices

Reuse `OPENAI_SECRET_KEY` and the existing ElevenLabs key configuration
(`RealtimeTranslation:ElevenLabs:ApiKey` or legacy `Elevenlabs_key`; `Speech:ApiKey`
is also bound by the speech options). Set `Avatar:VoiceId` to choose a voice;
otherwise the existing `Speech:DefaultVoiceId` is used. The server uses Scribe v2
Realtime, the existing OpenAI client/model, and Eleven v4 Turbo.

Customer rates come from the existing pricing resolver and speech options:
Scribe credits/minute divided by 60 for submitted audio seconds; Assistant token
rate and model multiplier for replies; speech character rate for submitted text.
Admins pay the same rates. Recognition reserves up to 45 seconds per utterance;
unused credit is released. Speech reserves the generated reply before synthesis.
Audio submission is recorded per frame under an operation-specific lock, shared
with settlement. It does not lock other sessions or pre-charge unsent seconds.
Only submitted provider inputs are charged after interruption; ambiguous network
outcomes may include the last submitted input. Six-decimal ledger precision applies.
The provider budget uses the configured Scribe cost and
`Avatar:SynthesisSekPerMillionCharacters` (a conservative ceiling independent of
customer prices). Review that ceiling against the account rate before rollout.

`AvatarUsageOperation` stores accounting metadata, including submitted units,
rate snapshots and reservation expiry. It stores no conversation text or audio.
Expired operations settle after five minutes through the maintenance worker.
Conversation context is process-local, capped at 24 messages/24,000 serialized
characters. Sessions expire after 30 minutes; microphone turns after 45 seconds;
unconnected sessions after 30 seconds; inactive sockets after 90 seconds. Switching
away from the browser page ends capture. In hands-free mode, silence sent to
recognition counts as usage. An utterance limit requires an explicit retry. Push-to-talk preserves up to five
seconds of microphone input during recognition setup, including a release before
setup completes; longer setup delays stop with a retry message. Turn deadlines
also report an explicit retry without closing the conversation.

The first version supports the language intersection declared in `AvatarController`.
Quiz practice uses up to 40 words and 20 sentences from an owned quiz, without
changing quiz attempts or study progress. Interrupted replies are omitted from
conversation memory until playback is acknowledged. No conversation transcript or
audio is persisted by GlobeGlotter.

## Rebuild the viewer

```sh
cd scripts/avatar
npm ci --ignore-scripts
npm run build
```

Commit `scene.min.js` and `scene.min.js.LEGAL.txt` with source changes. Dependencies are pinned
in the lockfile; the generated bundle is loaded only on the avatar page. Babylon.js
is Apache-2.0 licensed; linked license comments accompany the bundle.

## Rebuild Rain

The source is [Rain v3.3](https://studio.blender.org/characters/rain/v3/) from Blender
Studio, licensed CC BY 4.0. The page and distributed model directory retain credits.
Download the official archive:
`https://studio.blender.org/download-source/files/ee/a7/eea73e55dba1cea31c09848df6a794b2-4.zip`.

Archive SHA-256: `80217f163f6392dc829233d63c2cfb5e1376775bc34101ad14f39631fea70d24`.
Extract it outside the repository, then run:

```sh
blender --factory-startup -b --disable-autoexec '/path/Rain v3.3/rain_v3.2.blend' \
  -P scripts/avatar/export-rain.py -- Glosify/wwwroot/models/avatar/rain.glb
```

The conversion evaluates the production rig without running its embedded scripts,
keeps about 40,000 vertices, and bakes jaw, blink, smile, nod and turn poses into
morph targets. Textures and simplified PBR materials are embedded. Runtime mouth
movement follows the output audio amplitude and spectrum; it is not phoneme-exact
lip sync. The export preserves the authored character rather than recreating it.

## Tests

```sh
dotnet test Glosify.Tests --filter 'FullyQualifiedName~Avatar'
node --test Glosify.ClientTests/avatar-audio.test.js
```

Set `RUN_SQLSERVER_TESTS=true` and a local `ConnectionStrings__DefaultConnection`
to run the SQL Server concurrency case. It uses a disposable database, never the
configured application database. The live provider smoke test is opt-in:

```sh
RUN_AVATAR_LIVE_SMOKE=true AVATAR_SMOKE_PCM_PATH=/path/short-mono-16khz-pcm16.raw \
  dotnet test Glosify.Tests --filter 'FullyQualifiedName~AvatarLiveSmokeTests'
```

The live test uses local user-secret/environment keys and one short provider round
trip. It charges real provider usage but uses an isolated in-memory test ledger.

`browser-review.js` runs through Playwright CLI against a local HTTPS test host
using `AvatarFixture` with the simulated providers from `AvatarVoiceTests`. Seed an
admin cookie using that fixture, launch Chrome with a fake microphone and
`ignoreHTTPSErrors`, then run `playwright-cli run-code --filename
scripts/avatar/browser-review.js`. It exercises the real browser/server WebSocket,
including microphone capture, PTT, playback acknowledgement, hands-free, mute, end,
mobile layout and reduced motion. Screenshots go in `output/playwright/`.

No deployment, remote configuration, or production account changes are part of
this feature branch. `codex/talking-avatar-pr` is based on master (`ff7a1f8`)
and contains only the avatar implementation and review fixes. The original
`codex/talking-avatar` branch remains available with its isolated snapshot baseline;
do not use that original branch for a PR to master.
