# Stylized avatar preview

This local viewer loads the separately built likeness character. It reuses the
app's animation, morph-binding, meshopt decoder, and render scheduler, with the
existing library background. This builder now supplies Rowan’s shipped model.
The viewer does not call any speech or conversation service.

Prepare the pinned source assets with `scripts/avatar/prepare-human.py` as
described in the parent README. The additional round-head target is pinned in
`human-source.json`; existing source caches need that file as well.

From the repository root:

```sh
npm ci --prefix scripts/avatar
mkdir -p output/likeness-avatar/models output/likeness-avatar/renders
blender --background --factory-startup --python-exit-code 1 \
  --python scripts/avatar/build-likeness.py -- \
  /path/to/avatar-human-source \
  output/likeness-avatar/models/likeness-raw.glb \
  output/likeness-avatar/renders/portrait.png
node scripts/avatar/compress-model.mjs \
  output/likeness-avatar/models/likeness-raw.glb \
  output/likeness-avatar/models/likeness.glb
node scripts/avatar/preview/build.mjs
node scripts/avatar/preview/serve.mjs
```

Open `http://127.0.0.1:5201/`. The server listens only on loopback, accepts
`127.0.0.1` or `localhost` Host headers at its port, and exposes a fixed set of
preview assets. An optional port is the server's first argument.
Generated models, renders, and the viewer bundle stay in the ignored `output/`
directory. The optional PNG argument also saves a `.blend` scene for inspection.

The preview supports idle/listening/thinking states, smile/neutral expression
selection, blink/speaking demonstrations, three camera angles, and audio-driven lip sync from a
local file. The speaking-movement button is silent; it is not a synthesized voice.
The model has a neutral base face. The viewer defaults to the full symmetric
Smile expression at rest. Smile and Neutral select the resting expression.
Other facial emotion and speech remnants are cleared on returning to Idle.
Audio remains in the browser and is released on navigation. Idle movement respects
the reduced-motion preference and pauses when hidden.

The model carries all 23 production morph names. Check both front and
three-quarter views, eyelid closure, mouth shapes, hair/neck alignment, and the
GG shield during breathing when reviewing changes. The result is an art
directed character, not a face scan; the photo is not bundled or used as a texture.

Anatomy, short02 hair, brows, eyes, and the source suit are MakeHuman CC0 assets,
as documented in `Glosify/wwwroot/models/avatar/MAKEHUMAN-LICENSE.md`. The skin
painting, open-collar details, GG shield, and proportion changes are authored by
the builder. Babylon.js and meshoptimizer use the repository's pinned versions;
the viewer bundle retains linked third-party license notices.
