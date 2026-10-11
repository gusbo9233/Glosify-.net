# Avatar voice connection check

Initial connection checks and the follow-up below were performed locally on 2026-10-11.
The initial recovery changes were deployed; this follow-up is not deployed.

## Findings and changes

Previously, every browser disconnect removed the session and its context. A healthy
but silent conversation also hit the 90-second receive timeout. The browser had
no heartbeat, socket-opening deadline, or reconnection path; HTTP cleanup could
leave Start disabled while waiting for an unavailable server.

The client now sends five-second heartbeats, detects a silent connection after
20 seconds, limits socket setup to ten seconds, and retries transport with
1/2/4/8-second capped backoff for at most 45 seconds. Capture and playback pause
when a failure is detected. Restored conversations stay muted until Unmute;
interrupted audio is discarded, and the user repeats the interrupted turn.
The transport buffer is bounded at six seconds of PCM, with room for the existing
five-second setup buffer to drain without a false disconnect.

The server cancels and cleans up the previous turn before allowing another socket.
It retains process-local context for a 45-second reconnect grace period. Ownership,
origin, identity and single-connection checks still apply, as does the original
30-minute expiry. End and revoked authorization remove the session. Receive/send
deadlines and native WebSocket keepalives detect stalled browser/provider sockets.
Provider failures require an explicit retry, avoiding automatic paid resubmission.

HTTP requests and microphone flushing have deadlines. End is single-flight;
late microphone permission cannot restart capture, and late usage responses cannot
overwrite a newer conversation.

## Verification

- 96 relevant .NET tests passed, covering Avatar behavior and navigation/security
  headers; five existing live-provider/SQL Server opt-in cases were skipped.
- All 171 client tests passed, including fake-clock heartbeat/retry deadlines,
  dropped sockets, backpressure, stale events, muted recovery, microphone cleanup,
  and HTTP timeout recovery.
- Local browser checks use real browser/application WebSockets, a fake microphone,
  and simulated speech/reply providers. A sustained offline period exhausted the
  reconnect window, stopped the microphone track, released Start, and allowed a new
  conversation after the network returned. A brief socket drop rejoined the same
  session without resubmission, kept capture muted, and completed a new spoken turn
  after explicit Unmute.

No paid provider requests were made for this check. Simulated recognition and
synthesis WebSocket failures exercise the application recovery path; this does not
measure current ElevenLabs uptime or latency. Recovery cannot retain context across
an application restart, and disconnected sessions are recoverable only within the
grace period. Navigating away from the page continues to end capture deliberately.


## Follow-up: background tabs, first press and later visits

Switching tabs no longer ends the voice connection or stops playback. A held PTT
utterance is released to avoid leaving capture held when key/pointer release
happens in another tab. Hidden 3D rendering remains paused. Browser suspension,
network loss and the existing connection lifetime can still interrupt a session;
normal reconnect handling remains in place.

The first PTT press now opens the connection and buffers the utterance during
setup. Page load alone never opens the microphone. Navigating away ends capture,
but a later visit can use the account-scoped opaque sessionStorage reference to
restore server memory. Changing partner/language/quiz starts fresh. Memory is bounded, expires after eight hours of inactivity and is
lost on process restart. Only heard/acknowledged assistant replies are remembered.


Follow-up validation: all 189 client tests and 113 affected .NET tests passed;
five opt-in live-provider/SQL checks were skipped. The .NET WebSocket tests verify
that a later visit receives the prior context without automatically submitting a
paid turn, including omission of interrupted assistant replies. Client regressions
cover startup buffering/flush ordering, denied microphone permission, account
isolation, selection changes, page teardown and stale connection cleanup.

A headed browser against the isolated in-memory test host, with fake microphone
and simulated providers, verified first-press speech, End stopping the mic, leaving
and returning with two remembered turns, partner changes discarding old context,
New conversation clearing captions/memory, and mobile width without overflow.
No page errors occurred. This automated browser kept `document.hidden=false` even
when another tab was brought forward, so the hidden event was injected for that
check; the real microphone track and WebSocket stayed live. This is not a mobile
OS suspension guarantee. The temporary host and browser were stopped afterwards.


The subsequent UI cleanup removes pricing/balance details, the New conversation
button and explanatory memory text from conversation settings. Automatic memory
and resetting when the selection changes remain; usage totals remain below the
scene. The earlier New conversation check describes the now-removed control.


## Single recording control

The scene now uses one microphone button. Default PTT requires holding on desktop
and mobile; releasing commits the captured utterance. Pointer cancellation, lost
capture, keyboard focus loss and hiding the tab also release a held utterance.
The focused button supports Space/Enter; the old page-wide Space shortcut and
visible hint are removed along with the separate Mute/End buttons. Hands-free is
still an explicit choice, and the same microphone button pauses/resumes it.
Reconnect remains paused until this button is used. Changing partner/quiz closes
the previous connection automatically, allowing selection changes without End.


Single-control verification: 195 client tests passed, lint passed, and the preview
build completed without warnings/errors. Headed Chromium verified mouse hold /
release, a 72px mobile touch target using touchStart/touchEnd, hands-free pause,
reconnect resuming through the microphone, and partner changes stopping the old
microphone track. The recovery notice opened the settings drawer; the browser
check continued after closing that drawer. Providers and microphone were simulated.
