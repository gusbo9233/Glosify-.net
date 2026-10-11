# Avatar voice connection check

Checked locally on 2026-10-11. This change is not deployed.

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
grace period. Switching away from the page continues to end capture deliberately.
