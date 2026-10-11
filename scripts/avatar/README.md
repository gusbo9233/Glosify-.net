# Avatar assets and verification

The admin-only `/Avatar` page is implemented inside the Glosify MVC application.
It ships with `Avatar:Enabled=true`; apply `AddAvatarUsageOperations` through the normal deployment migration bundle before serving traffic. The existing
`Admin:UserIds` allowlist controls the page, HTTP API, and WebSocket. No shared game
authentication or separate game deployment is required.

## Conversation controls and memory

Push-to-talk is the default on every page visit. The first hold of the microphone
button requests microphone permission and opens the voice connection; there is
no separate Start action. Choose Hands-free explicitly to use continuous capture.
Merely loading the page does not open the microphone or submit provider requests.
The scene has one circular microphone control: hold to record, release to send on
both desktop and mobile. The mobile target is 72 pixels; the focused button also
supports holding Space/Enter. In explicitly selected Hands-free mode, the same
button pauses/resumes capture. There are no separate Mute/End controls or Space hint.

Leaving the page stops capture. Returning in the same tab/browser session restores
conversation context on the next talk action. Only an opaque reference and the
selected partner/quiz/language are kept in account-scoped `sessionStorage`; the
transcript remains in process-local server memory. Changing partner, language or
quiz discards the previous reference instead of keeping separate conversations.
Changing partner or quiz stops the old capture automatically. Captions show only turns heard during the current visit.

Server memory is owner/context-checked, bounded to 128 conversations and 24 turns /
24,000 serialized characters each, and expires after eight hours without a saved
turn. Capacity eviction and application restarts can also discard it. An old
connection cannot overwrite memory after a newer visit has claimed it. Existing
30-minute connection limits and 45-second transport recovery limits still apply.

## Providers and prices

See [the Rain/Rowan casting sheet](voice-casting.md) for the researched 69-language
voice pairings, provider previews, and age/pronunciation caveats. Both 69-language
mappings ship in `Glosify/appsettings.json`. All 137 unique voices were verified in
the deployment account on 2026-10-10 after adding 92 missing library voices.

Reuse `OPENAI_SECRET_KEY` and the existing ElevenLabs key configuration
(`RealtimeTranslation:ElevenLabs:ApiKey` or legacy `Elevenlabs_key`; `Speech:ApiKey`
is also bound by the speech options). The key also needs read access to the voices catalogue.
Choose Rain or Rowan before starting a conversation. Rain uses a youthful female voice (teenage tones are allowed);
Rowan uses an adult male voice. Selection uses native-language labels first, then
verified languages, then a multilingual fallback of the chosen gender. Saved voice names never
establish language support. Catalogue pagination is followed and results are cached for
one hour in separate gender caches; selection is pinned before the session begins and before any paid provider work.
Custom-rate and live-moderated voices are excluded. The UI identifies a multilingual
fallback when there is no native-labelled match; native accent quality still needs listening
review. No voices are automatically added at runtime. The Georgian pair is a multilingual
fallback; PCM synthesis passed, but native pronunciation still needs listening review.

The shipped `Avatar:VoiceIds:<catalog-language-code>` mappings choose a specific available
female voice for one language; `Avatar:MaleVoiceIds:<catalog-language-code>` does the
same for Rowan. An unavailable matching voice prevents the session from starting.
The previous global `Avatar:VoiceId` and the book reader's
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
unconnected sessions after 30 seconds. Switching tabs keeps the conversation,
microphone and playback running while pausing hidden rendering. A held
push-to-talk utterance is released when the tab becomes hidden. Navigating away
from the page or closing it still ends capture. In hands-free mode, silence sent to
recognition counts as usage. An utterance limit requires an explicit retry. Push-to-talk preserves up to five
seconds of microphone input during recognition setup, including a release before
setup completes; longer setup delays stop with a retry message. Turn deadlines
also report an explicit retry without closing the conversation.

The browser sends a heartbeat every five seconds and detects a silent connection
within 20 seconds; opening a socket has a ten-second deadline. A lost connection
pauses capture and playback immediately when detected, then retries with capped
backoff for up to 45 seconds. The server retains the owned session and conversation
context for 45 seconds after the old connection finishes cancelling its turn.
Reconnect still checks authentication, origin and session ownership, allows only
one socket, and never extends the original 30-minute lifetime. Restored sessions
keep the microphone paused until the user uses the microphone button again. Interrupted audio and
provider requests are never automatically replayed; repeat the interrupted turn.
Server receive/send deadlines and WebSocket ping/pong also bound stalled sockets.
Recognition or synthesis failure reports an explicit retry on the current session.
End, expired authorization and session expiry remove the session; process restart
loses process-local context. HTTP setup and cleanup have deadlines, and late usage
responses cannot update a later conversation. See the [connection check](connection-review.md)
for failure scenarios and verification limits.

Conversations use the current learning language from the app’s language context;
there is no separate avatar language picker or fixed preview allowlist. The server
resolves the language again when starting a session and ignores any client language
override. Quiz choices and practice are limited to owned quizzes in that language,
including aliases and legacy quiz language fields. With no selected learning language
(or in Freestyle), choose a language through the existing sidebar selector first.
Quiz practice uses up to 40 words and 20 sentences from an owned quiz, without
changing quiz attempts or study progress. Interrupted replies are omitted from
conversation memory until playback is acknowledged. No conversation transcript or
audio is written to the database; text memory is temporary as described above.

The conversation settings use the app theme and can be collapsed with the side
arrow. The page expands up to 1800px on wide screens, with a 300–360px settings
panel and a landscape library backdrop; closing settings gives the scene the full
width. Captions stay within a readable 48rem measure. At 1100px and below, settings
open as a scrollable side drawer; Escape closes the drawer
and returns focus to the arrow. Notices reopen settings so connection errors remain
visible. The portrait has no idle caption; captions appear only for conversation text.

## Rendering cost

Visible portraits keep subtle breathing, blinks and posture movement at 12 fps
before/after a conversation, between turns and in error state. Active listening,
connecting and thinking run at 15 fps; speech runs at 24 fps. Hidden/offscreen
portraits pause; reduced motion also pauses settled non-speaking states. The
canvas drawing buffer is capped at 1,000 pixels on its longest edge.
See [the performance check](performance-review.md) for measurements and remaining
model-loading costs.

## Rebuild the viewer

```sh
cd scripts/avatar
npm ci --ignore-scripts
npm run build
```

Commit `scene.min.js`, `meshopt.worker.js`, and their `.LEGAL.txt` notices with source changes. Dependencies are pinned
in the lockfile; the generated bundle is loaded only on the avatar page. Babylon.js
is Apache-2.0 licensed; linked license comments accompany the bundle.
The meshoptimizer 0.24.0 decoder is MIT-licensed and bundled into a 27 KB local
worker. `meshopt.js` registers the glTF loader adapter, so no CDN scripts, blob
workers or network decoder downloads are used. Each model load owns one worker
and terminates it when loading finishes or fails. Only the exact worker script
response permits WebAssembly in its CSP; pages keep `script-src 'self'`, and the
worker cannot fetch resources or create nested workers.

## Rebuild the characters

Both characters use CC0 MakeHuman adult anatomy, skin, eyes and clothing, with original
portrait styling. `academia_outfit.py` adds an ivory pointed collar, forest-green
woven-check vest, black ribbon tie and an antique-gold embroidered GG chest patch.
The outfit combines the green vest/ivory shirt and ribbon/check details from the
user's two outfit references (`img/girl-outfit.webp` and `img/girl-outfit2` in the
main working tree); no photo pixels are included in the shipped assets. The patch
is intentionally partly covered by her hair. Garments inherit the blouse's skin
weights before morph baking, including breathing and shoulder movement.
Rowan has male anatomy, short CC0 MakeHuman hair, and a waist-length charcoal
V-neck cardigan with fitted sleeves, fine knit textures, ribbed front borders and
dark horn buttons. An ivory shirt and dark necktie show inside the V, with a gold
GG shield embroidered on the cardigan chest. His raised collar stand wraps the neck;
the shirt shoulders fill the neckline, and the smaller collar points frame the tie
knot. Shirt and tie weights blend from the torso to the neck for body morphs.
The character chooser swaps the model and pins the character name and matching voice
for the conversation; it is locked while a model loads or a conversation is active.
Layered Hazel Hair by Elvaerwyn is CC-BY; the model license file
and page credits include attribution and the source link.
Restoring the page through browser Back/Forward reloads the selected model and current
configuration without restarting the microphone. Late model loads from the previous
page visit are discarded. The core CC0 dedication
does not apply to this hair. Asset attribution and license are distributed
with the model. `human-source.json` pins the upstream revision, download URLs and hashes.
Prepare the listed files into an external source directory (archive files under `system/`):

```sh
python3 scripts/avatar/prepare-human.py /tmp/avatar-human-source
blender --factory-startup -b -P scripts/avatar/build-rain.py -- \
  /tmp/avatar-human-source /tmp/avatar-rain-raw.glb
blender --factory-startup -b -P scripts/avatar/build-rain.py -- \
  /tmp/avatar-human-source /tmp/avatar-rowan-raw.glb --character rowan
```

Then compress the fresh exports as the final asset step:

```sh
node scripts/avatar/compress-model.mjs /tmp/avatar-rain-raw.glb Glosify/wwwroot/models/avatar/rain-realistic.glb
node scripts/avatar/compress-model.mjs /tmp/avatar-rowan-raw.glb Glosify/wwwroot/models/avatar/rowan-realistic.glb
npm run build --prefix scripts/avatar
```

The pinned glTF Transform pipeline uses Meshopt high compression with 14-bit
positions, 10-bit normals and 12-bit UVs; UVs outside [0,1] remain unquantized.
It keeps mesh topology, textures, materials and named morph targets. Compression
is lossy quantization, so keep raw exports outside `wwwroot` and never repeatedly
compress shipped files. The script rejects compressed inputs and identical
input/output paths. The pre-compression assets are also retained in Git at
`0d02d69`. Current shipped sizes are Rain 3.69 MB and Rowan 3.00 MB, down from
15.37 MB and 12.21 MB. See [validation and measurements](performance-review.md).

An optional final PNG path renders a portrait and saves a review `.blend` beside it.
The fit helper comes from the game's MakeHuman anatomy builder; no addon is required.
The export bakes morph targets for mouth shapes (`jawOpen`, `mouthWide`, `mouthRound`,
`lipsPart`, `mouthPress`), expressions (`smileLeft`/`Right`, `browRaiseLeft`/`Right`,
`browInner`, `browDown`, `squint`, `eyeWide`, `blink`), gaze (`lookUp`/`Down`/`Left`/`Right`)
and body language (`nod`, `turn`, `tilt`, `shrug`, `breathe`), and embeds textures. After
export, morph deltas below 0.01 mm (positions) or 0.001 (normals) are dropped and stored
as sparse accessors, so the extra units add about 0.6 MB instead of over 20 MB.
Facial sculpt targets soften the jaw and define the upper lip. The resting smile
has closed lips and no jaw-opening pose; expression smiles change slowly with the
conversation state and never follow the audio, so speech returns to a calm closed mouth.
The hair is weighted to the head,
avoiding shoulder-driven kinks during posing. The face retains all source bone weights
before baking so facial expressions are not truncated to four influences.
The resting smile, cheeks, eyebrows, eyelids, gaze and speaking mouth use the authored CC0 face pose
units in `face-poses.json`, whose source revision is recorded alongside the poses.

`Glosify/wwwroot/js/avatar/performance.js` drives these morphs and is bundled into
`scene.min.js`. Mouth shapes come from the reply audio's spectrum: loudness and energy
near 1 kHz open the jaw, 2–3 kHz energy spreads the lips, its absence rounds them and
hiss parts them. This is approximate, not phoneme-exact lip sync. When texture morphs are unavailable,
the renderer budgets vertex attributes and keeps a stable reduced set, prioritizing
speech, blinking and symmetric smiles rather than dropping arbitrary movements. Both characters also hold eye
contact with small saccades and occasional glances, blink irregularly (sometimes twice),
nod on speech emphasis, give small nods when the learner pauses, look away while
thinking, raise their brows after asking a question, and breathe. With reduced motion,
blinks, gaze, head and posture movement stop; the mouth still follows speech.

## Rebuild the library background

`wwwroot/images/avatar/library.jpg` is rendered in Blender: walnut bookcases filled with
generated books, tobacco-olive plaster, deep green drapes around an arched window,
and an antique floor globe
beside a reading chair, softly out of focus behind the character. The 2560 × 1440 landscape
render combines subdued cool window light with warm amber practicals for a dark
academia room; portrait screens crop the same room around its central window.
Furniture, chandelier, lamp,
clock, books and wood textures are CC0 Poly Haven assets; the globe restyles the public
domain NASA Blue Marble land map (credit: Reto Stöckli, NASA Goddard Space Flight Center).
`library-source.json` pins every download and its hash; the build fetches and verifies
missing files, then renders (about five minutes on a laptop CPU):

```sh
blender --factory-startup -b -P scripts/avatar/build-library.py -- \
  /tmp/avatar-library-source Glosify/wwwroot/images/avatar/library.jpg
```

Optional arguments after the output set the resolution percentage and sample count for
quick previews. The background carries an `academia-1` cache revision; both Meshopt models use
`meshopt-1`. Bump the relevant revision and rebuild the viewer when replacing an asset.
The view through the window is `wwwroot/images/avatar/courtyard.jpg`, an
AI-generated image. Prompt:
“Premium polished 3D Italian courtyard loggia, no people or text; warm ivory arches,
jasmine, olive foliage and terracotta planters, soft Tuscan garden, late afternoon
sunlight from upper left, uncluttered center for a brunette portrait, sage/cream palette.”

## Tests

```sh
dotnet test Glosify.Tests --filter 'FullyQualifiedName~Avatar'
node --test Glosify.ClientTests/avatar-*.test.js
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

## Deployment verification

On `codex/avatar-current-language`, Release verification passed: 1,253 .NET tests
(40 opt-in skips), all 147 client tests, the live catalogue check for every
assignment, and six short live synthesis checks. The Release publish artifact
contains both models, their attribution, the viewer, library background and all
voice mappings. Production uses the same ElevenLabs key and already enables the
avatar; no host settings were changed and no application deployment was made.

To recheck a different provider account, supply its key through the normal secret
configuration and run the read-only catalogue check. The synthesis check spends a
small amount of real provider credit and uses only an isolated test ledger:

```sh
RUN_AVATAR_CATALOGUE_SMOKE=true dotnet test Glosify.Tests -c Release \
  --filter 'FullyQualifiedName~AvatarCastingSmokeTests.EveryShippedVoice'
RUN_AVATAR_CASTING_SMOKE=true dotnet test Glosify.Tests -c Release \
  --filter 'FullyQualifiedName~AvatarCastingSmokeTests.SelectedVoiceProducesPcm'
dotnet publish Glosify/Glosify.csproj -c Release --output /tmp/glosify-avatar-publish
```

If the provider account changes, add the missing selections before rollout; the
casting JSON includes their library links. Host-level dictionary entries override
the shipped IDs. Roll back access with `Avatar__Enabled=false`. Keep the existing
single-instance deployment rule and admin allowlist.
