import { StreamingPcm16Downsampler, pcm16ToBase64 } from "../lib/audio-pcm.js";
import { normalizeRealtimeEvent } from "../lib/realtime-events.js";
import { buildRelayProtocols, buildRelayWebSocketUrl } from "../lib/relay-url.js";

const RELAY_CONNECT_TIMEOUT_MS = 15_000;
const MAX_RELAY_BUFFERED_BYTES = 512 * 1024;

let sourceStream = null;
let audioContext = null;
let sourceNode = null;
let processorNode = null;
let mutedProcessorOutput = null;
let downsampler = null;
let relaySocket = null;
let relayReady = false;
let relayAuthorizedUntil = 0;
let tickTimer = null;
let expectedRelayClose = false;
let expectedCaptureEnd = false;
let relayErrorReported = false;
let sequence = 0;
let currentSessionId = null;
let currentTargetLanguage = null;

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
    case "media:start":
      await stopAll();
      await startCapture(message.streamId);
      await connectRelay(message);
      return { connected: true };
    case "media:reconnect":
      if (!sourceStream?.active) {
        throw new Error("The captured tab audio is no longer available.");
      }
      await disconnectRelay();
      await connectRelay(message);
      return { connected: true };
    case "media:authorize-minute":
      authorizeMinute(message);
      return { authorized: true };
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
  const audioTrack = sourceStream.getAudioTracks()[0];
  if (!audioTrack) {
    throw new Error("The selected tab does not expose an audio track.");
  }

  audioTrack.addEventListener("ended", () => {
    if (!expectedCaptureEnd) {
      chrome.runtime.sendMessage({ type: "media:ended" }).catch(() => {});
    }
  });

  // tabCapture mutes the tab's normal output. Route the captured stream back to
  // the local destination, then separately encode a muted branch for Foundry.
  audioContext = new AudioContext();
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
    chrome.runtime.sendMessage({ type: "media:tick" }).catch(() => {});
  }, 1000);
}

function processAudio(event) {
  if (!relayReady
      || Date.now() >= relayAuthorizedUntil
      || relaySocket?.readyState !== WebSocket.OPEN
      || relaySocket.bufferedAmount > MAX_RELAY_BUFFERED_BYTES) {
    return;
  }

  const pcm = downsampler.process(event.inputBuffer.getChannelData(0));
  if (pcm.length === 0) {
    return;
  }
  relaySocket.send(JSON.stringify({
    type: "session.input_audio_buffer.append",
    audio: pcm16ToBase64(pcm),
  }));
}

async function connectRelay({ sessionId, targetLanguage, relayToken, relayPath, glosifyBaseUrl }) {
  const relayUrl = buildRelayWebSocketUrl(glosifyBaseUrl, relayPath, sessionId);
  const protocols = buildRelayProtocols(relayToken);
  expectedRelayClose = false;
  relayErrorReported = false;
  relayReady = false;
  relayAuthorizedUntil = 0;
  currentSessionId = sessionId;
  currentTargetLanguage = targetLanguage;
  sequence = 0;

  const socket = new WebSocket(relayUrl, protocols);
  relaySocket = socket;
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      cleanup();
      socket.close();
      reject(new Error("Timed out while connecting to Glosify live subtitles."));
    }, RELAY_CONNECT_TIMEOUT_MS);

    const cleanup = () => {
      clearTimeout(timeout);
      socket.removeEventListener("message", onMessage);
      socket.removeEventListener("close", onClose);
      socket.removeEventListener("error", onError);
    };
    const onMessage = event => {
      let providerEvent;
      try {
        providerEvent = JSON.parse(event.data);
      } catch {
        return;
      }
      if (providerEvent?.type === "glosify.relay.ready") {
        cleanup();
        relayReady = true;
        resolve();
      } else if (providerEvent?.type === "glosify.relay.error") {
        cleanup();
        reject(new Error(providerEvent.message || "Glosify ended the subtitle relay."));
      }
    };
    const onClose = () => {
      cleanup();
      reject(new Error("The Glosify subtitle relay closed while connecting."));
    };
    const onError = () => {
      cleanup();
      reject(new Error("The Glosify subtitle relay could not be reached."));
    };

    socket.addEventListener("message", onMessage);
    socket.addEventListener("close", onClose);
    socket.addEventListener("error", onError);
  });

  if (socket.protocol !== "glosify-realtime") {
    await disconnectRelay();
    throw new Error("Glosify returned an invalid subtitle relay protocol.");
  }

  socket.onmessage = handleRelayMessage;
  socket.onerror = () => reportRelayFailure("The Glosify subtitle relay encountered a network error.");
  socket.onclose = () => {
    relayReady = false;
    relayAuthorizedUntil = 0;
    if (!expectedRelayClose) {
      reportRelayFailure("The Glosify subtitle relay ended.");
    }
  };
}

function handleRelayMessage({ data }) {
  try {
    const providerEvent = JSON.parse(data);
    if (providerEvent?.type === "glosify.relay.error") {
      reportRelayFailure(providerEvent.message || "Glosify ended the subtitle relay.");
      return;
    }
    if (providerEvent?.type === "glosify.transcript.warning") {
      chrome.runtime.sendMessage({
        type: "media:storage-warning",
        message: providerEvent.message,
      }).catch(() => {});
      return;
    }
    const normalized = normalizeRealtimeEvent(providerEvent, {
      sessionId: currentSessionId,
      targetLanguage: currentTargetLanguage,
      nextSequence: () => ++sequence,
    });
    if (normalized) {
      chrome.runtime.sendMessage({ type: "media:event", event: normalized }).catch(() => {});
    }
  } catch {
    // Ignore malformed/non-JSON provider events; raw event content is never logged.
  }
}

function authorizeMinute({ sessionId, minuteIndex, sessionStartedAt }) {
  if (sessionId !== currentSessionId
      || !Number.isInteger(minuteIndex)
      || minuteIndex < 1
      || !Number.isFinite(sessionStartedAt)
      || sessionStartedAt <= 0) {
    throw new Error("Glosify returned invalid minute authorization.");
  }
  relayAuthorizedUntil = sessionStartedAt + minuteIndex * 60_000;
}

function reportRelayFailure(error) {
  if (expectedRelayClose || relayErrorReported) {
    return;
  }
  relayErrorReported = true;
  chrome.runtime.sendMessage({ type: "media:error", error }).catch(() => {});
}

async function disconnectRelay() {
  expectedRelayClose = true;
  relayReady = false;
  relayAuthorizedUntil = 0;
  const socket = relaySocket;
  relaySocket = null;
  if (socket && socket.readyState < WebSocket.CLOSING) {
    socket.close(1000, "Subtitle relay disconnected.");
  }
  currentSessionId = null;
  currentTargetLanguage = null;
}

async function stopAll() {
  expectedCaptureEnd = true;
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
  downsampler = null;
  if (audioContext && audioContext.state !== "closed") {
    await audioContext.close();
  }
  audioContext = null;
}
