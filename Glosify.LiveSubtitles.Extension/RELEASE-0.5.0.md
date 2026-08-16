# Glosify Live Subtitles 0.5.0 BETA release checklist

## Server-first deployment

- [ ] Deploy the additive web/backend build before uploading the extension.
- [ ] Verify `/healthz`, realtime session creation, reserve, begin, heartbeat, and deletion in production.
- [ ] Confirm heartbeat responses include `sessionStartedAtUtc` and `audioSendAuthorizedUntilUtc` while 0.4.0 sessions still start and stop normally.
- [ ] Confirm telemetry receives session starts, terminal statuses, worker recoveries, dropped-audio milliseconds, and backpressure events without user, session, URL, caption, or audio data.

## Headed production Chrome

Run both Scribe and Enhanced modes in a clean headed Chrome profile:

- [ ] Start and stop subtitles; confirm tab audio remains audible and the offscreen document closes after stop.
- [ ] Reload, same-origin navigate, and cross-origin navigate; each full navigation must stop capture. Confirm History API same-document navigation continues.
- [ ] Complete two paid-minute rollovers and verify uninterrupted PCM and correct credit charges.
- [ ] Terminate the MV3 worker during an active authorized session and verify recovery. Repeat with deliberately mismatched stored/offscreen state and verify fail-closed cleanup.
- [ ] Enable transcript saving, verify the opt-in transcript, delete it, and verify a new session starts with saving disabled.
- [ ] Sign out during and after capture; verify media, relay, backend session, overlay, offscreen state, and trusted session storage are cleared.
- [ ] Exercise provider failure, paid-services budget closure, insufficient credits, and sustained backpressure; verify a safe stop without a replacement billable minute.

## Store submission and monitoring

- [ ] Run `npm test`, `npm run test:browser`, and `npm run package:store` from this directory.
- [ ] Upload only `artifacts/package/glosify-live-subtitles-0.5.0-beta.zip`.
- [ ] Select **Public** visibility and retain **Glosify Live Subtitles BETA** branding.
- [ ] Recheck the Store privacy declarations and the navigation-stop/degraded-connection copy.
- [ ] Use deferred publishing only after the backend verification and headed checks pass.

For the first 24 hours, disable realtime translation with the existing feature flag if any threshold is crossed:

- start success below 95%;
- unexpected interruptions above 5%;
- backpressure stops above 2%; or
- any verified capture that continues after a full navigation.
