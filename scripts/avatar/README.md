# Avatar assets and verification

The admin-only `/Avatar` page is implemented inside the Glosify MVC application.
Enable `Avatar:Enabled` after applying `AddAvatarUsageOperations`. The existing
`Admin:UserIds` allowlist controls the page, HTTP API, and WebSocket. No shared game
authentication or separate game deployment is required.

## Providers and prices

Reuse `OPENAI_SECRET_KEY` and the existing ElevenLabs key configuration
(`RealtimeTranslation:ElevenLabs:ApiKey` or legacy `Elevenlabs_key`; `Speech:ApiKey`
is also bound by the speech options). The key also needs read access to the voices catalogue.
The avatar selects an available adult female voice using native-language labels first,
then verified languages, then a multilingual female fallback. Saved voice names never
establish language support. Catalogue pagination is followed and results are cached for
one hour; selection is pinned before the session begins and before any paid provider work.
Custom-rate and live-moderated voices are excluded. The UI identifies a multilingual
fallback when there is no native-labelled match; native accent quality still needs listening
review. No voices are automatically added to the provider account.

Optional `Avatar:VoiceIds:<catalog-language-code>` overrides choose a specific available
female voice for one language. The previous global `Avatar:VoiceId` and the book reader's
`Speech:DefaultVoiceId` no longer control avatar speech. Synthesis explicitly sends the
current language (including `zh-Hans` → `zh` and `nb` → `no`). The server uses Scribe v2
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

Conversations use the current learning language from the app’s language context;
there is no separate avatar language picker or fixed preview allowlist. The server
resolves the language again when starting a session and ignores any client language
override. Quiz choices and practice are limited to owned quizzes in that language,
including aliases and legacy quiz language fields. With no selected learning language
(or in Freestyle), choose a language through the existing sidebar selector first.
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

Rain now uses realistic adult anatomy and CC0 MakeHuman skin, eyes, long hair and
clothing, with original portrait styling. Asset attribution and license are distributed
with the model. `human-source.json` pins the upstream revision, download URLs and hashes.
Prepare the listed files into an external source directory (archive files under `system/`):

```sh
python3 scripts/avatar/prepare-human.py /tmp/avatar-human-source
blender --factory-startup -b -P scripts/avatar/build-rain.py -- \
  /tmp/avatar-human-source Glosify/wwwroot/models/avatar/rain-realistic.glb
```

An optional final PNG path renders a portrait and saves a review `.blend` beside it.
The fit helper comes from the game's MakeHuman anatomy builder; no addon is required.
The export bakes jaw, blink, smile, nod and turn into morph targets and embeds textures.
The resting smile, cheeks, eyelids and speaking mouth use the authored CC0 face pose
units in `face-poses.json`, whose source revision is recorded alongside the poses. Runtime mouth
movement follows output audio amplitude and spectrum; it is not phoneme-exact lip sync.

`wwwroot/images/avatar/courtyard.jpg` is an AI-generated, optimized background. Prompt:
“Premium polished 3D Italian courtyard loggia, no people or text; warm ivory arches,
jasmine, olive foliage and terracotta planters, soft Tuscan garden, late afternoon
sunlight from upper left, uncluttered center for a brunette portrait, sage/cream palette.”

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
