import {
  StreamingPcm16Downsampler,
  accumulateSkippedAudioMilliseconds,
  pcm16ToBase64,
} from "../lib/audio-pcm.js";
import { createRealtimeEventAccumulator } from "../lib/realtime-events.js";
import { buildRelayProtocols, buildRelayWebSocketUrl } from "../lib/relay-url.js";

const RELAY_CONNECT_TIMEOUT_MS = 15_000;
const RELAY_DISCONNECT_TIMEOUT_MS = 6_000;
const MAX_RELAY_BUFFERED_BYTES = 512 * 1024;
const MAX_AUTHORIZATION_AHEAD_MS = 61 * 60_000;
const BACKPRESSURE_STOP_MS = 2_000;

let sourceStream = null;
let audioContext = null;
let sourceNode = null;
let processorNode = null;
let mutedProcessorOutput = null;
let testOscillator = null;
let downsampler = null;
let relayConnection = null;
let relayGeneration = 0;
let tickTimer = null;
let expectedCaptureEnd = false;
let backpressureActive = false;
let continuousDroppedAudioMs = 0;
let unreportedDroppedAudioMs = 0;

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.target !== "offscreen") {
    return undefined;
  }

  handleMessage(message)
    .then(result => sendResponse({ ok: true, result }))
    .catch(error => sendResponse({ ok: false, error: error?.message || "Audio capture failed." }));
  return true;
});

async function handleMessage(message) {
  switch (message.type) {
    case "media:start-capture":
      await stopAll();
      await startCapture(message.streamId);
      return { capturing: true };
    case "media:start-test-capture":
      await stopAll();
      await startTestCapture();
      return { capturing: true };
    case "media:connect-relay":
      if (!sourceStream?.active) {
        throw new Error("The captured tab audio is no longer available.");
      }
      await connectRelay(message);
      return { connected: true };
    case "media:reconnect":
      if (!sourceStream?.active) {
        throw new Error("The captured tab audio is no longer available.");
      }
      await disconnectRelay();
      await connectRelay(message);
      return { connected: true };
    case "media:authorize-until":
      authorizeUntil(message);
      return { authorized: true };
    case "media:get-state":
      return mediaState();
    case "media:disconnect-relay":
      await disconnectRelay();
      return { disconnected: true };
    case "media:stop":
      await stopAll();
      return { stopped: true };
    default:
      return null;
  }
}

async function startCapture(streamId) {
  expectedCaptureEnd = false;
  sourceStream = await navigator.mediaDevices.getUserMedia({
    audio: {
      mandatory: {
        chromeMediaSource: "tab",
        chromeMediaSourceId: streamId,
      },
    },
    video: false,
  });
  await initializeAudioGraph();
}

async function startTestCapture() {
  expectedCaptureEnd = false;
  audioContext = new AudioContext();
  const destination = audioContext.createMediaStreamDestination();
  testOscillator = audioContext.createOscillator();
  testOscillator.frequency.value = 220;
  testOscillator.connect(destination);
  testOscillator.start();
  sourceStream = destination.stream;
  await initializeAudioGraph(audioContext);
}

async function initializeAudioGraph(existingAudioContext = null) {
  const audioTrack = sourceStream.getAudioTracks()[0];
  if (!audioTrack) {
    throw new Error("The selected tab does not expose an audio track.");
  }

  audioTrack.addEventListener("ended", () => {
    if (!expectedCaptureEnd) {
      chrome.runtime.sendMessage({ type: "media:ended" }).catch(() => {});
    }
  });

  // tabCapture mutes the tab's normal output. Route the stream back to the
  // local destination, then encode a separate muted branch for the relay.
  audioContext ??= existingAudioContext ?? new AudioContext();
  sourceNode = audioContext.createMediaStreamSource(sourceStream);
  sourceNode.connect(audioContext.destination);

  downsampler = new StreamingPcm16Downsampler(audioContext.sampleRate);
  processorNode = audioContext.createScriptProcessor(4096, 1, 1);
  mutedProcessorOutput = audioContext.createGain();
  mutedProcessorOutput.gain.value = 0;
  sourceNode.connect(processorNode);
  processorNode.connect(mutedProcessorOutput);
  mutedProcessorOutput.connect(audioContext.destination);
  processorNode.onaudioprocess = processAudio;
  await audioContext.resume();

  tickTimer = setInterval(() => {
    flushIdleEvents();
    chrome.runtime.sendMessage({ type: "media:tick" }).catch(() => {});
  }, 1000);
}

function processAudio(event) {
  const connection = relayConnection;
  if (!isCurrentConnection(connection)
      || !connection.ready
      || connection.errorReported
      || performance.now() >= connection.authorizedUntilMonotonic
      || connection.socket.readyState !== WebSocket.OPEN) {
    return;
  }

  if (connection.socket.bufferedAmount > MAX_RELAY_BUFFERED_BYTES) {
    const previousDroppedAudioMs = continuousDroppedAudioMs;
    continuousDroppedAudioMs = accumulateSkippedAudioMilliseconds(
      continuousDroppedAudioMs,
      event.inputBuffer.length,
      audioContext.sampleRate);
    const droppedMs = continuousDroppedAudioMs - previousDroppedAudioMs;
    unreportedDroppedAudioMs += droppedMs;
    if (!backpressureActive) {
      backpressureActive = true;
      chrome.runtime.sendMessage({
        type: "media:degraded",
        active: true,
        droppedAudioMilliseconds: 0,
        backpressureEvents: 1,
      }).catch(() => {});
    }
    if (continuousDroppedAudioMs >= BACKPRESSURE_STOP_MS) {
      reportBackpressureDiagnostics(true);
      reportRelayFailure(connection, "The subtitle connection was too slow, so audio capture stopped.");
    }
    return;
  }

  if (backpressureActive) {
    reportBackpressureDiagnostics(false);
  }
  const pcm = downsampler.process(event.inputBuffer.getChannelData(0));
  if (pcm.length === 0) {
    return;
  }
  connection.socket.send(JSON.stringify({
    type: "session.input_audio_buffer.append",
    audio: pcm16ToBase64(pcm),
  }));
}

async function connectRelay({
  sessionId,
  targetLanguage,
  partialCaptionsEnabled = true,
  relayToken,
  relayPath,
  glosifyBaseUrl,
  allowInsecureRelay,
}) {
  if (relayConnection) {
    throw new Error("The previous subtitle relay is still connected.");
  }
  const relayUrl = buildRelayWebSocketUrl(
    glosifyBaseUrl,
    relayPath,
    sessionId,
    { allowInsecure: allowInsecureRelay === true });
  const protocols = buildRelayProtocols(relayToken);
  const generation = ++relayGeneration;
  const socket = new WebSocket(relayUrl, protocols);
  const connection = {
    socket,
    generation,
    sessionId,
    targetLanguage,
    partialCaptionsEnabled: partialCaptionsEnabled !== false,
    ready: false,
    sessionStartedAt: 0,
    authorizedUntil: 0,
    authorizedUntilMonotonic: 0,
    expectedClose: false,
    errorReported: false,
    error: null,
    sequence: 0,
    accumulator: null,
  };
  connection.accumulator = createRealtimeEventAccumulator({
    sessionId,
    targetLanguage,
    nextSequence: () => ++connection.sequence,
  }, {
    partialCaptionsEnabled: connection.partialCaptionsEnabled,
  });
  relayConnection = connection;

  try {
    await waitForRelayReady(connection);
    if (!isCurrentConnection(connection)) {
      throw new Error("The subtitle relay was replaced while connecting.");
    }
    if (socket.protocol !== "glosify-realtime") {
      throw new Error("Glosify returned an invalid subtitle relay protocol.");
    }
    connection.ready = true;
    socket.onmessage = event => handleRelayMessage(event, connection);
    socket.onerror = () => reportRelayFailure(
      connection,
      "The Glosify subtitle relay encountered a network error.");
    socket.onclose = () => {
      if (!isCurrentConnection(connection)) {
        return;
      }
      connection.ready = false;
      connection.authorizedUntil = 0;
      connection.authorizedUntilMonotonic = 0;
      if (!connection.expectedClose) {
        reportRelayFailure(connection, "The Glosify subtitle relay ended.");
      }
    };
  } catch (error) {
    if (isCurrentConnection(connection)) {
      await disconnectRelay();
    }
    throw error;
  }
}

function waitForRelayReady(connection) {
  return new Promise((resolve, reject) => {
    const { socket } = connection;
    let settled = false;
    const timeout = setTimeout(() => finish(new Error(
      isCurrentConnection(connection)
        ? "Timed out while connecting to Glosify live subtitles."
        : "The subtitle relay was replaced while connecting.")), RELAY_CONNECT_TIMEOUT_MS);
    const finish = error => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timeout);
      socket.removeEventListener("message", onMessage);
      socket.removeEventListener("close", onClose);
      socket.removeEventListener("error", onError);
      error ? reject(error) : resolve();
    };
    const onMessage = event => {
      if (!isCurrentConnection(connection)) {
        return;
      }
      let providerEvent;
      try {
        providerEvent = JSON.parse(event.data);
      } catch {
        return;
      }
      if (providerEvent?.type === "glosify.relay.ready") {
        finish();
      } else if (providerEvent?.type === "glosify.relay.error") {
        finish(new Error(providerEvent.message || "Glosify ended the subtitle relay."));
      }
    };
    const onClose = () => {
      finish(new Error(isCurrentConnection(connection)
        ? "The Glosify subtitle relay closed while connecting."
        : "The subtitle relay was replaced while connecting."));
    };
    const onError = () => {
      finish(new Error(isCurrentConnection(connection)
        ? "The Glosify subtitle relay could not be reached."
        : "The subtitle relay was replaced while connecting."));
    };
    socket.addEventListener("message", onMessage);
    socket.addEventListener("close", onClose);
    socket.addEventListener("error", onError);
  });
}

function handleRelayMessage({ data }, connection) {
  if (!isCurrentConnection(connection)) {
    return;
  }
  let providerEvent;
  try {
    providerEvent = JSON.parse(data);
  } catch {
    // Ignore malformed/non-JSON provider events; raw event content is never logged.
    return;
  }
  if (providerEvent?.type === "glosify.relay.error") {
    reportRelayFailure(connection, boundedRelayMessage(
      providerEvent.message,
      "Glosify ended the subtitle relay."));
    return;
  }
  if (providerEvent?.type === "glosify.relay.closed") {
    for (const event of connection.accumulator.flushAll()) {
      sendNormalizedEvent(event, connection);
    }
    return;
  }
  if (providerEvent?.type === "glosify.transcript.warning") {
    chrome.runtime.sendMessage({
      type: "media:storage-warning",
      message: boundedRelayMessage(
        providerEvent.message,
        "Transcript storage is temporarily unavailable."),
    }).catch(() => {});
    return;
  }
  try {
    sendNormalizedEvent(connection.accumulator.apply(providerEvent), connection);
  } catch {
    reportRelayFailure(connection, "The subtitle relay returned an unsupported event sequence.");
  }
}

function boundedRelayMessage(value, fallback) {
  return typeof value === "string" && value.trim()
    ? value.trim().slice(0, 500)
    : fallback;
}

function flushIdleEvents() {
  const connection = relayConnection;
  if (!isCurrentConnection(connection)) {
    return;
  }
  for (const event of connection.accumulator.flushIdle()) {
    sendNormalizedEvent(event, connection);
  }
}

function sendNormalizedEvent(event, connection) {
  if (event && isCurrentConnection(connection)) {
    chrome.runtime.sendMessage({ type: "media:event", event }).catch(() => {});
  }
}

function authorizeUntil({
  sessionId,
  sessionStartedAtUtc,
  audioSendAuthorizedUntilUtc,
  serverNowUtc,
  maxSessionMinutes,
}) {
  const connection = relayConnection;
  const sessionStartedAt = Date.parse(sessionStartedAtUtc);
  const deadline = Date.parse(audioSendAuthorizedUntilUtc);
  const serverNow = Date.parse(serverNowUtc);
  const maximumMinutes = Number(maxSessionMinutes);
  if (!isCurrentConnection(connection)
      || sessionId !== connection.sessionId
      || !Number.isFinite(sessionStartedAt)
      || !Number.isFinite(deadline)
      || !Number.isFinite(serverNow)
      || !Number.isInteger(maximumMinutes)
      || maximumMinutes < 1
      || maximumMinutes > 60
      || serverNow + 2_000 < sessionStartedAt
      || deadline <= serverNow
      || deadline > serverNow + MAX_AUTHORIZATION_AHEAD_MS
      || deadline > sessionStartedAt + maximumMinutes * 60_000 + 2_000
      || (connection.sessionStartedAt && connection.sessionStartedAt !== sessionStartedAt)
      || deadline < connection.authorizedUntil) {
    throw new Error("Glosify returned invalid minute authorization.");
  }
  connection.sessionStartedAt = sessionStartedAt;
  connection.authorizedUntil = deadline;
  connection.authorizedUntilMonotonic = performance.now() + (deadline - serverNow);
}

function reportRelayFailure(connection, error) {
  if (!isCurrentConnection(connection)
      || connection.expectedClose
      || connection.errorReported) {
    return;
  }
  connection.errorReported = true;
  connection.error = error;
  chrome.runtime.sendMessage({ type: "media:error", error }).catch(() => {});
}

function reportBackpressureDiagnostics(stillActive) {
  chrome.runtime.sendMessage({
    type: "media:degraded",
    active: stillActive,
    droppedAudioMilliseconds: unreportedDroppedAudioMs,
    backpressureEvents: 0,
  }).catch(() => {});
  unreportedDroppedAudioMs = 0;
  if (!stillActive) {
    backpressureActive = false;
    continuousDroppedAudioMs = 0;
  }
}

async function disconnectRelay() {
  const connection = relayConnection;
  if (!connection) {
    return;
  }
  const { socket } = connection;
  const shouldDrain = connection.ready && socket.readyState === WebSocket.OPEN;
  connection.expectedClose = true;
  connection.ready = false;
  connection.authorizedUntil = 0;
  connection.authorizedUntilMonotonic = 0;
  try {
    if (socket.readyState !== WebSocket.CLOSED) {
      await new Promise(resolve => {
        let settled = false;
        const finish = () => {
          if (settled) {
            return;
          }
          settled = true;
          clearTimeout(timeout);
          socket.removeEventListener("message", onMessage);
          socket.removeEventListener("close", finish);
          resolve();
        };
        const timeout = setTimeout(() => {
          if (socket.readyState < WebSocket.CLOSING) {
            socket.close(1000, "Subtitle relay drain timed out.");
          }
          finish();
        }, RELAY_DISCONNECT_TIMEOUT_MS);
        const onMessage = event => {
          let providerEvent;
          try {
            providerEvent = JSON.parse(event.data);
          } catch {
            return;
          }
          if (providerEvent?.type === "glosify.relay.closed"
              && socket.readyState < WebSocket.CLOSING) {
            socket.close(1000, "Subtitle relay disconnected.");
          }
        };
        socket.addEventListener("message", onMessage);
        socket.addEventListener("close", finish);
        if (shouldDrain) {
          socket.send(JSON.stringify({ type: "glosify.relay.close" }));
        } else if (socket.readyState < WebSocket.CLOSING) {
          socket.close(1000, "Subtitle relay disconnected.");
        } else if (socket.readyState >= WebSocket.CLOSING) {
          finish();
        }
      });
    }
  } finally {
    socket.onmessage = null;
    socket.onerror = null;
    socket.onclose = null;
    if (relayConnection === connection) {
      ++relayGeneration;
      relayConnection = null;
    }
  }
}

async function stopAll() {
  expectedCaptureEnd = true;
  if (backpressureActive && unreportedDroppedAudioMs > 0) {
    reportBackpressureDiagnostics(false);
  }
  await disconnectRelay();
  if (tickTimer) {
    clearInterval(tickTimer);
    tickTimer = null;
  }
  processorNode && (processorNode.onaudioprocess = null);
  processorNode?.disconnect();
  processorNode = null;
  mutedProcessorOutput?.disconnect();
  mutedProcessorOutput = null;
  sourceNode?.disconnect();
  sourceNode = null;
  sourceStream?.getTracks().forEach(track => track.stop());
  sourceStream = null;
  testOscillator?.stop();
  testOscillator?.disconnect();
  testOscillator = null;
  downsampler = null;
  if (audioContext && audioContext.state !== "closed") {
    await audioContext.close();
  }
  audioContext = null;
  backpressureActive = false;
  continuousDroppedAudioMs = 0;
  unreportedDroppedAudioMs = 0;
}

function mediaState() {
  const connection = relayConnection;
  return {
    captureActive: Boolean(sourceStream?.active),
    sessionId: connection?.sessionId ?? null,
    targetLanguage: connection?.targetLanguage ?? null,
    partialCaptionsEnabled: connection?.partialCaptionsEnabled ?? null,
    relayReady: Boolean(connection?.ready && connection.socket.readyState === WebSocket.OPEN),
    sessionStartedAtUtc: connection?.sessionStartedAt
      ? new Date(connection.sessionStartedAt).toISOString()
      : null,
    audioSendAuthorizedUntilUtc: connection?.authorizedUntil
      ? new Date(connection.authorizedUntil).toISOString()
      : null,
    backpressureActive,
  };
}

function isCurrentConnection(connection) {
  return Boolean(connection
    && relayConnection === connection
    && relayGeneration === connection.generation);
}
