# Avatar performance check — 2026-10-11

The main avoidable cost was continuous 3D rendering before a conversation started
and after it ended. The existing 40 fps throttle delivered approximately 30 fps
on the tested 60 Hz display. Reduced motion suppressed movement but did not stop
rendering; scrolling the portrait out of view also left the renderer running.

The glTF loader additionally disabled morph influencer optimization. As a result,
even zero-weight expression targets were processed. The texture path now enables
optimization while retaining the full shader capacity, preserving all expressions
and avoiding shader variants as the active target count changes. The installed
Babylon.js 9.25.0 shader uses `morphTargetCount` to terminate its loop; this was
checked against the installed loader, manager and shader source, alongside the
[official morph-target documentation](https://doc.babylonjs.com/features/featuresDeepDive/mesh/morphTargets).
The existing attribute-mode compatibility budget is unchanged.

## Changes

- Before/after a conversation and in error state, render briefly to settle the
  expression, then stop scheduling frames. The portrait remains visible.
- During conversation, render speech at 24 fps and other states at 15 fps.
- Suspend rendering when the document is hidden or the canvas is offscreen.
- With reduced motion, settle non-speaking states and retain mouth animation
  during speech. React to preference changes without reloading.
- Cap the drawing buffer's longest edge at 1,000 pixels, preserving the existing
  high-DPI downscaling. An 1800 × 820 CSS canvas uses a 1000 × 455 buffer.
  This trades some sharpness on wide screens for less pixel work.
- Disable picking for the non-interactive portrait, and skip zero-weight texture
  morphs. Dispose the scheduler and observers when changing characters/leaving.

## Measurements

Local headed Chromium, WebGL2/ANGLE Metal on Apple M1, 60 Hz, 1000 × 720 canvas.
An isolated static viewer loaded the actual models and renderer source bundled
with its pinned dependencies. A temporary diagnostic exposed the scene and
wrapped `scene.render` to count frames. Browser task time is the delta of CDP
`Performance.getMetrics` / `TaskDuration` over five seconds after a two-second
warmup. No speech providers, microphone, authentication or paid calls were used.

| Rain, five-second sample | Before | After |
| --- | ---: | ---: |
| Waiting to start: rendered frames | 150 | 0 |
| Waiting to start: browser task time | 223.4 ms | 0.7 ms |
| Speaking state: rendered frames | 151 | 121 |
| Speaking state: browser task time | 256.8 ms | 232.1 ms |

The speaking timing sample did not supply audio. Separate functional browser
checks supplied a synthetic analyser spectrum and verified jaw movement for both
Rain and Rowan. Both rendered 29 frames in 1.2 seconds during speech, zero while
offscreen, resumed with 29 frames, and stopped in reduced-motion non-speaking
state. Resizing respected the buffer limit; disposal stopped work; no JavaScript
or WebGL errors occurred. The exact minified shipping bundle was also checked for
initial rendering, settled idle, speech-state wakeup and disposal for both models.

These are single local samples, not measurements of the user's older laptop,
whole-system CPU, GPU time, thermal load or battery use. Active-state CPU timing
is noisy; the deterministic improvement is eliminating idle frames and reducing
active frame/pixel/morph work. The full authenticated voice conversation and
server were not profiled in this check.

## Remaining costs

| Asset | Size (decimal MB) | Triangles | Vertices |
| --- | ---: | ---: | ---: |
| Rain | 15.37 | 183,082 | 124,911 |
| Rowan | 12.21 | 136,574 | 90,435 |

The viewer bundle is approximately 3.49 MB uncompressed. Model geometry, textures
and download sizes were not changed. Cold loading, decoding, shader compilation
and GPU memory may still be expensive on old hardware. A separately authored
lower-detail portrait/model would be the next step if active conversation still
causes unacceptable load; this needs visual comparison and morph validation.

## Regression checks

```sh
node --test Glosify.ClientTests/avatar-*.test.js
npm test --prefix Glosify.ClientTests
npm run build --prefix scripts/avatar
```

All 32 Avatar client tests and all 153 client tests passed. Scheduler checks cover
frame rates, settled idle, resize invalidation, visibility, reduced motion,
error state and disposal. Existing audio/conversation and WebGL attribute-budget
checks also passed. No server-side code changed; .NET tests were not rerun.


## Follow-up: shipped Meshopt compression

Both shipped GLBs now use `EXT_meshopt_compression` / `KHR_mesh_quantization`.
The original asset sizes and rendering measurements above describe the initial
performance check before this follow-up.

| Asset | Original GLB | Meshopt GLB | Reduction | Meshopt + Brotli (quality 5) |
| --- | ---: | ---: | ---: | ---: |
| Rain | 15.37 MB | 3.69 MB | 76.0% | 2.54 MB |
| Rowan | 12.21 MB | 2.89 MB | 76.4% | 1.90 MB |

Brotli values are local compression measurements, not a production transfer
measurement. The local decoder worker adds 27 KB uncompressed. Images and triangle
counts are unchanged. Compression targets loading/network cost; it does not
reduce the model's ongoing rendering complexity.

`compress-model.mjs` makes the process reproducible with pinned dependencies and
rejects already-compressed input. The runtime decoder uses a same-origin worker
with embedded WASM. The worker response alone permits `wasm-unsafe-eval`, with
network access and nested workers blocked. The page CSP does not change.
This follows the [worker CSP model](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy)
and [Meshopt compression guidance](https://gltf-transform.dev/modules/extensions/classes/EXTMeshoptCompression).

Browser verification uses Chromium/WebGL2 with those CSP headers enforced:
original and compressed models retain identical per-mesh vertex/index counts
and morph names. Neutral, speech, blink/smile and gaze/brow poses were compared with animation stopped.
Across those full-frame screenshots, mean absolute RGB differences were below
0.10 on a 0–255 channel scale for Rain and below 0.03 for Rowan.
No remote requests or JavaScript errors occurred, and model loading released the
decoder worker. Synthetic speech still animates both characters; idle, offscreen,
reduced-motion and disposal behavior remain covered.

Validation: 156 client tests passed. The affected .NET suites ran with
`dotnet test Glosify.Tests --no-build --filter 'FullyQualifiedName~NavigationTests|FullyQualifiedName~Avatar'`:
Passed: 89, Failed: 0, Skipped: 5 (opt-in provider/SQL tests).
This includes response-header tests proving only the worker can compile WASM.
No deployment or real speech-provider requests were performed.
