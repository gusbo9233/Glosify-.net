import { CONFIG } from "../config.js";
import { getBillingAction } from "../lib/billing.js";
import {
  buildTranscriptSessionRequest,
  canSaveSourceTranscript,
  clearTranscriptStorageState,
  getEffectiveCreditsPerMinute,
} from "../lib/transcript-storage.js";

const STORAGE_KEYS = Object.freeze({
  refreshToken: "glosifyRefreshToken",
  targetLanguage: "glosifyTargetLanguage",
});

let accessToken = null;
let accessExpiresAt = 0;
let refreshToken = null;
let billingBusy = false;
let heartbeatBusy = false;
let stopping = false;

const state = {
  status: "disconnected",
  signedIn: false,
  email: null,
  availableCredits: 0,
  catalog: null,
  targetLanguage: "en",
  bilingualAvailable: false,
  bilingualEnabled: false,
  saveTranscript: false,
  tabId: null,
  sessionId: null,
  transcriptId: null,
  currentMinute: 0,
  nextMinuteReserved: false,
  stopAtBoundary: false,
  sessionStartedAt: 0,
  connectionStartedAt: 0,
  lastHeartbeatAt: 0,
  firstCaptionLatencyMs: null,
  firstCaptionReported: false,
  reconnectReported: false,
  error: null,
  notice: null,
};

const initialization = restoreLocalState();

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.target === "offscreen" || message.target === "popup") {
    return undefined;
  }

  handleMessage(message, sender)
    .then(result => sendResponse({ ok: true, result }))
    .catch(error => {
      const normalized = normalizeError(error);
      sendResponse({ ok: false, error: normalized.message, status: normalized.status });
    });
  return true;
});

chrome.tabs.onRemoved.addListener(tabId => {
  if (tabId === state.tabId) {
    void stopSession("The captured tab was closed.", "error");
  }
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (tabId !== state.tabId || changeInfo.status !== "complete" || !state.sessionId) {
    return;
  }
  void ensureContentOverlay(tabId).then(async () => {
    await sendToTab({ type: "overlay:mode", bilingualEnabled: state.bilingualEnabled });
    await sendToTab({ type: "overlay:status", text: statusText() });
  });
});

async function handleMessage(message) {
  await initialization;
  switch (message.type) {
    case "popup:get-state":
      if (refreshToken && !state.sessionId) {
        await refreshAccountState();
      }
      return publicState();
    case "popup:sign-in":
      await signIn();
      return publicState();
    case "popup:sign-out":
      await signOut();
      return publicState();
    case "popup:refresh":
      await refreshAccountState();
      return publicState();
    case "popup:set-target":
      await setTargetLanguage(message.targetLanguage);
      return publicState();
    case "popup:set-bilingual":
      await setBilingual(Boolean(message.enabled));
      return publicState();
    case "popup:set-save-transcript":
      setSaveTranscript(Boolean(message.enabled));
      return publicState();
    case "popup:open-transcripts":
      await chrome.tabs.create({ url: new URL("/Transcripts", CONFIG.glosifyBaseUrl).toString() });
      return publicState();
    case "popup:open-languages":
      await chrome.tabs.create({ url: new URL("/Languages?returnUrl=%2FTranscripts", CONFIG.glosifyBaseUrl).toString() });
      return publicState();
    case "popup:start":
      await startSession();
      return publicState();
    case "popup:stop":
      await stopSession(null, "ready");
      return publicState();
    case "media:event":
      await handleSubtitleEvent(message.event);
      return null;
    case "media:tick":
      await processTick();
      return null;
    case "media:storage-warning":
      state.notice = message.message || "Live subtitles are continuing, but the saved transcript may be incomplete.";
      await sendToTab({ type: "overlay:status", text: state.notice });
      broadcastState();
      return null;
    case "media:error":
      if (!stopping) {
        await stopSession(message.error || "The Microsoft Foundry connection ended.", "error");
      }
      return null;
    case "media:ended":
      if (!stopping) {
        await stopSession("The captured tab stopped sharing audio.", "error");
      }
      return null;
    default:
      return null;
  }
}

async function restoreLocalState() {
  await chrome.storage.local.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
  const stored = await chrome.storage.local.get(Object.values(STORAGE_KEYS));
  refreshToken = stored[STORAGE_KEYS.refreshToken] ?? null;
  state.targetLanguage = stored[STORAGE_KEYS.targetLanguage] ?? "en";
  state.signedIn = Boolean(refreshToken);
  state.status = refreshToken ? "ready" : "disconnected";
}

async function signIn() {
  const codeVerifier = base64Url(crypto.getRandomValues(new Uint8Array(32)));
  const codeChallenge = base64Url(new Uint8Array(
    await crypto.subtle.digest("SHA-256", new TextEncoder().encode(codeVerifier))));
  const oauthState = base64Url(crypto.getRandomValues(new Uint8Array(24)));
  const redirectUri = chrome.identity.getRedirectURL("glosify");
  const authorizeUrl = new URL("/extension/connect", CONFIG.glosifyBaseUrl);
  authorizeUrl.searchParams.set("redirect_uri", redirectUri);
  authorizeUrl.searchParams.set("state", oauthState);
  authorizeUrl.searchParams.set("code_challenge", codeChallenge);
  authorizeUrl.searchParams.set("code_challenge_method", "S256");

  state.status = "connecting";
  state.error = null;
  broadcastState();
  try {
    const callbackUrl = await chrome.identity.launchWebAuthFlow({
      url: authorizeUrl.toString(),
      interactive: true,
    });
    if (!callbackUrl) {
      throw new Error("Glosify sign-in was cancelled.");
    }

    const callback = new URL(callbackUrl);
    if (callback.searchParams.get("state") !== oauthState) {
      throw new Error("Glosify sign-in returned an invalid state value.");
    }
    const code = callback.searchParams.get("code");
    if (!code) {
      throw new Error(callback.searchParams.get("error") || "Glosify sign-in did not return a code.");
    }

    const response = await fetch(new URL("/api/extension-auth/exchange", CONFIG.glosifyBaseUrl), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      cache: "no-store",
      body: JSON.stringify({ code, redirectUri, codeVerifier }),
    });
    if (!response.ok) {
      throw await apiError(response);
    }
    await acceptTokenResponse(await response.json());
    await refreshAccountState();
  } catch (error) {
    state.status = refreshToken ? "ready" : "disconnected";
    state.error = normalizeError(error).message;
    broadcastState();
    throw error;
  }
}

async function signOut() {
  if (state.sessionId) {
    await stopSession(null, "ready");
  }
  accessToken = null;
  accessExpiresAt = 0;
  refreshToken = null;
  await chrome.storage.local.remove(STORAGE_KEYS.refreshToken);
  Object.assign(state, {
    status: "disconnected",
    signedIn: false,
    email: null,
    availableCredits: 0,
    catalog: null,
    saveTranscript: false,
    transcriptId: null,
    error: null,
    notice: null,
  });
  broadcastState();
}

async function acceptTokenResponse(tokens) {
  if (!tokens?.accessToken || !tokens?.refreshToken) {
    throw new Error("Glosify returned an invalid token response.");
  }
  accessToken = tokens.accessToken;
  accessExpiresAt = Date.now() + Math.max(30, Number(tokens.expiresIn ?? 3600) - 30) * 1000;
  refreshToken = tokens.refreshToken;
  await chrome.storage.local.set({ [STORAGE_KEYS.refreshToken]: refreshToken });
  state.signedIn = true;
}

async function ensureAccessToken() {
  if (accessToken && Date.now() < accessExpiresAt) {
    return accessToken;
  }
  if (!refreshToken) {
    throw new ApiRequestError(401, "Connect your Glosify account first.");
  }

  const response = await fetch(new URL("/api/auth/refresh", CONFIG.glosifyBaseUrl), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    cache: "no-store",
    body: JSON.stringify({ refreshToken }),
  });
  if (!response.ok) {
    await clearExpiredAuthentication();
    throw new ApiRequestError(401, "Your Glosify session expired. Connect again.");
  }
  await acceptTokenResponse(await response.json());
  return accessToken;
}

async function clearExpiredAuthentication() {
  accessToken = null;
  accessExpiresAt = 0;
  refreshToken = null;
  await chrome.storage.local.remove(STORAGE_KEYS.refreshToken);
  state.signedIn = false;
  state.status = "disconnected";
  state.catalog = null;
}

async function apiFetch(path, options = {}, retry = true) {
  const token = await ensureAccessToken();
  const headers = new Headers(options.headers ?? {});
  headers.set("Authorization", `Bearer ${token}`);
  if (options.body !== undefined && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }
  const response = await fetch(new URL(path, CONFIG.glosifyBaseUrl), {
    ...options,
    headers,
    cache: "no-store",
  });
  if (response.status === 401 && retry) {
    accessToken = null;
    accessExpiresAt = 0;
    await ensureAccessToken();
    return apiFetch(path, options, false);
  }
  if (!response.ok) {
    throw await apiError(response);
  }
  if (response.status === 204) {
    return null;
  }
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

async function apiError(response) {
  let message = `Glosify request failed (${response.status}).`;
  try {
    const body = await response.json();
    message = body.error ?? body.detail ?? body.title ?? message;
  } catch {
    // Do not surface raw upstream responses; they can contain implementation details.
  }
  return new ApiRequestError(response.status, message);
}

async function refreshAccountState() {
  if (!refreshToken) {
    state.signedIn = false;
    state.status = "disconnected";
    broadcastState();
    return;
  }

  try {
    const me = await apiFetch("/api/me");
    state.signedIn = true;
    state.email = me.email;
    state.availableCredits = me.availableCredits;
    const catalog = await apiFetch("/api/realtime-translation/catalog");
    state.catalog = catalog;
    state.availableCredits = catalog.availableCredits;
    if (!catalog.languages.some(language => language.code === state.targetLanguage)) {
      state.targetLanguage = catalog.languages[0]?.code ?? "en";
      await chrome.storage.local.set({ [STORAGE_KEYS.targetLanguage]: state.targetLanguage });
    }
    if (!state.sessionId && state.saveTranscript && !canSaveTranscript()) {
      state.saveTranscript = false;
      state.transcriptId = null;
    }
    if (!state.sessionId) {
      state.status = "ready";
    }
    state.error = null;
  } catch (error) {
    const normalized = normalizeError(error);
    if (normalized.status !== 401) {
      state.signedIn = true;
      state.status = state.sessionId ? state.status : "error";
      state.error = normalized.message;
    }
  }
  broadcastState();
}

async function setTargetLanguage(targetLanguage) {
  if (state.sessionId) {
    throw new Error("Stop the current subtitles before changing language.");
  }
  if (!state.catalog?.languages.some(language => language.code === targetLanguage)) {
    throw new Error("Choose a supported target language.");
  }
  state.targetLanguage = targetLanguage;
  if (state.saveTranscript && !canSaveTranscript()) {
    state.saveTranscript = false;
    state.transcriptId = null;
  }
  await chrome.storage.local.set({ [STORAGE_KEYS.targetLanguage]: targetLanguage });
  broadcastState();
}

async function setBilingual(enabled) {
  state.bilingualEnabled = enabled && state.bilingualAvailable;
  await sendToTab({ type: "overlay:mode", bilingualEnabled: state.bilingualEnabled });
  broadcastState();
}

function setSaveTranscript(enabled) {
  if (state.sessionId) {
    throw new Error("Stop the current subtitles before changing transcript storage.");
  }
  if (enabled && !canSaveTranscript()) {
    throw new Error(saveTranscriptUnavailableMessage());
  }
  state.saveTranscript = enabled;
  broadcastState();
}

async function startSession() {
  if (state.sessionId || state.status === "connecting") {
    return;
  }
  await refreshAccountState();
  if (!state.signedIn || !state.catalog) {
    throw new Error(state.error || "Connect your Glosify account first.");
  }
  if (state.saveTranscript && !canSaveTranscript()) {
    throw new Error(saveTranscriptUnavailableMessage());
  }
  const requiredCredits = effectiveCreditsPerMinute();
  if (state.availableCredits < requiredCredits) {
    throw new ApiRequestError(402, "You do not have enough Glosify credits to start subtitles.");
  }

  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id || !isCapturableUrl(tab.url)) {
    throw new Error("Open a regular web page such as Twitch or YouTube, then try again.");
  }

  state.status = "connecting";
  state.error = null;
  state.notice = null;
  state.tabId = tab.id;
  state.bilingualAvailable = false;
  state.bilingualEnabled = false;
  broadcastState();

  try {
    await ensureContentOverlay(tab.id);
    await sendToTab({ type: "overlay:status", text: "Connecting to Glosify…" });
    await ensureOffscreenDocument();
    const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tab.id });
    const created = await apiFetch("/api/realtime-translation/sessions", {
      method: "POST",
      body: JSON.stringify(buildTranscriptSessionRequest(state)),
    });

    state.sessionId = created.sessionId;
    state.transcriptId = created.transcriptId ?? null;
    state.availableCredits = created.availableCredits;
    state.connectionStartedAt = Date.now();
    state.firstCaptionLatencyMs = null;
    state.firstCaptionReported = false;
    state.reconnectReported = true;
    await sendToOffscreen({
      type: "media:start",
      streamId,
      sessionId: created.sessionId,
      targetLanguage: state.targetLanguage,
      relayToken: created.relayToken,
      relayPath: created.relayPath,
      glosifyBaseUrl: CONFIG.glosifyBaseUrl,
    });
    const begun = await apiFetch(
      `/api/realtime-translation/sessions/${created.sessionId}/minutes/1/begin`,
      { method: "POST" });
    state.availableCredits = begun.availableCredits;
    state.currentMinute = 1;
    state.nextMinuteReserved = false;
    state.stopAtBoundary = false;
    state.sessionStartedAt = Date.now();
    state.lastHeartbeatAt = Date.now();
    state.status = "subtitling";
    state.notice = null;
    await authorizeOffscreenMinute(1);
    await sendToTab({ type: "overlay:status", text: "Listening…" });
    broadcastState();
  } catch (error) {
    const normalized = normalizeError(error);
    await stopSession(
      normalized.message,
      normalized.status === 402 ? "insufficient_credits" : "error");
    throw error;
  }
}

async function handleSubtitleEvent(event) {
  if (!event || event.sessionId !== state.sessionId || !state.tabId) {
    return;
  }
  if (event.stream === "source" && event.delta) {
    state.bilingualAvailable = true;
    broadcastState();
  }
  if (event.stream === "translation" && event.delta && state.firstCaptionLatencyMs === null) {
    state.firstCaptionLatencyMs = Math.max(0, Date.now() - state.connectionStartedAt);
  }
  await sendToTab({
    type: "overlay:subtitle",
    event,
    bilingualEnabled: state.bilingualEnabled,
  });
}

async function processTick() {
  if (!state.sessionId || !state.sessionStartedAt || stopping) {
    return;
  }

  const now = Date.now();
  if (!heartbeatBusy
      && now - state.lastHeartbeatAt >= (state.catalog?.heartbeatSeconds ?? 15) * 1000) {
    heartbeatBusy = true;
    const heartbeatSessionId = state.sessionId;
    apiFetch(`/api/realtime-translation/sessions/${heartbeatSessionId}/heartbeat`, {
      method: "POST",
      body: JSON.stringify({
        firstCaptionLatencyMs: state.firstCaptionReported ? null : state.firstCaptionLatencyMs,
        reconnected: !state.reconnectReported,
      }),
    })
      .then(() => {
        if (state.sessionId !== heartbeatSessionId) {
          return;
        }
        state.lastHeartbeatAt = Date.now();
        state.firstCaptionReported ||= state.firstCaptionLatencyMs !== null;
        state.reconnectReported = true;
      })
      .catch(error => {
        if (state.sessionId === heartbeatSessionId) {
          state.notice = normalizeError(error).message;
          broadcastState();
        }
      })
      .finally(() => { heartbeatBusy = false; });
  }

  if (billingBusy) {
    return;
  }
  const action = getBillingAction({
    elapsedMs: now - state.sessionStartedAt,
    currentMinute: state.currentMinute,
    nextMinuteReserved: state.nextMinuteReserved,
    stopAtBoundary: state.stopAtBoundary,
    maxSessionMinutes: state.catalog?.maxSessionMinutes ?? 30,
    renewalLeadSeconds: state.catalog?.renewalLeadSeconds ?? 5,
  });
  if (action.type === "none") {
    return;
  }

  billingBusy = true;
  try {
    if (action.type === "reserve") {
      try {
        const result = await apiFetch(
          `/api/realtime-translation/sessions/${state.sessionId}/minutes/${action.minuteIndex}/reserve`,
          { method: "POST" });
        state.nextMinuteReserved = true;
        state.availableCredits = result.availableCredits;
      } catch (error) {
        if (normalizeError(error).status === 402) {
          state.stopAtBoundary = true;
          state.notice = "Subtitles will stop at the end of this paid minute: not enough credits.";
          await sendToTab({ type: "overlay:status", text: state.notice });
        } else {
          throw error;
        }
      }
      broadcastState();
    } else if (action.type === "begin") {
      const result = await apiFetch(
        `/api/realtime-translation/sessions/${state.sessionId}/minutes/${action.minuteIndex}/begin`,
        { method: "POST" });
      state.currentMinute = action.minuteIndex;
      state.nextMinuteReserved = false;
      state.availableCredits = result.availableCredits;
      state.notice = null;
      await authorizeOffscreenMinute(action.minuteIndex);
      broadcastState();
    } else if (action.type === "stop") {
      await stopSession(
        state.stopAtBoundary ? "Subtitles stopped because your Glosify credits ran out." : "The next minute was not authorized.",
        state.stopAtBoundary ? "insufficient_credits" : "error");
    } else if (action.type === "reconnect") {
      await reconnectAtSessionLimit();
    }
  } catch (error) {
    const normalized = normalizeError(error);
    await stopSession(
      normalized.status === 402
        ? "Subtitles stopped because your Glosify credits ran out."
        : normalized.message,
      normalized.status === 402 ? "insufficient_credits" : "error");
  } finally {
    billingBusy = false;
  }
}

async function reconnectAtSessionLimit() {
  const oldSessionId = state.sessionId;
  if (!oldSessionId) {
    return;
  }
  state.status = "reconnecting";
  state.notice = "Reconnecting…";
  broadcastState();
  await sendToTab({ type: "overlay:status", text: "Reconnecting…" });
  await sendToOffscreen({ type: "media:disconnect-relay" });
  await apiFetch(`/api/realtime-translation/sessions/${oldSessionId}`, { method: "DELETE" });
  state.sessionId = null;

  const created = await apiFetch("/api/realtime-translation/sessions", {
    method: "POST",
    body: JSON.stringify(buildTranscriptSessionRequest(state)),
  });
  state.sessionId = created.sessionId;
  state.transcriptId = created.transcriptId ?? state.transcriptId;
  state.connectionStartedAt = Date.now();
  state.firstCaptionLatencyMs = null;
  state.firstCaptionReported = false;
  state.reconnectReported = false;
  await sendToOffscreen({
    type: "media:reconnect",
    sessionId: created.sessionId,
    targetLanguage: state.targetLanguage,
    relayToken: created.relayToken,
    relayPath: created.relayPath,
    glosifyBaseUrl: CONFIG.glosifyBaseUrl,
  });
  const begun = await apiFetch(
    `/api/realtime-translation/sessions/${created.sessionId}/minutes/1/begin`,
    { method: "POST" });
  Object.assign(state, {
    status: "subtitling",
    availableCredits: begun.availableCredits,
    currentMinute: 1,
    nextMinuteReserved: false,
    stopAtBoundary: false,
    sessionStartedAt: Date.now(),
    lastHeartbeatAt: Date.now(),
    notice: null,
    bilingualAvailable: false,
    bilingualEnabled: false,
  });
  await authorizeOffscreenMinute(1);
  await sendToTab({ type: "overlay:mode", bilingualEnabled: false });
  await sendToTab({ type: "overlay:status", text: "Listening…" });
  broadcastState();
}

async function stopSession(message, finalStatus) {
  if (stopping) {
    return;
  }
  stopping = true;
  const sessionId = state.sessionId;
  try {
    try {
      await sendToOffscreen({ type: "media:stop" });
    } catch {
      // The offscreen document may already have closed after a capture failure.
    }
    if (sessionId && refreshToken) {
      try {
        await apiFetch(`/api/realtime-translation/sessions/${sessionId}`, { method: "DELETE" });
      } catch {
        // The cleanup service will release any pending reservation if this request cannot arrive.
      }
    }
    await sendToTab({ type: "overlay:clear" });
    Object.assign(state, {
      status: finalStatus === "ready" ? "ready" : finalStatus,
      tabId: null,
      sessionId: null,
      transcriptId: null,
      currentMinute: 0,
      nextMinuteReserved: false,
      stopAtBoundary: false,
      sessionStartedAt: 0,
      connectionStartedAt: 0,
      lastHeartbeatAt: 0,
      firstCaptionLatencyMs: null,
      firstCaptionReported: false,
      reconnectReported: false,
      bilingualAvailable: false,
      bilingualEnabled: false,
      saveTranscript: false,
      error: finalStatus === "error" || finalStatus === "insufficient_credits" ? message : null,
      notice: null,
    });
    clearTranscriptStorageState(state);
    if (refreshToken && finalStatus === "ready") {
      await refreshAccountState();
    } else {
      broadcastState();
    }
  } finally {
    stopping = false;
  }
}

async function ensureOffscreenDocument() {
  const documentUrl = chrome.runtime.getURL("offscreen/offscreen.html");
  const contexts = await chrome.runtime.getContexts({
    contextTypes: ["OFFSCREEN_DOCUMENT"],
    documentUrls: [documentUrl],
  });
  if (contexts.length > 0) {
    return;
  }
  await chrome.offscreen.createDocument({
    url: "offscreen/offscreen.html",
    reasons: ["USER_MEDIA", "AUDIO_PLAYBACK"],
    justification: "Capture tab audio, keep it audible locally, and stream it through Glosify for Foundry subtitles.",
  });
}

async function ensureContentOverlay(tabId) {
  try {
    await chrome.scripting.executeScript({
      target: { tabId },
      files: ["lib/chat-buffer.js", "content/subtitles.js"],
    });
  } catch {
    throw new Error("Chrome does not allow subtitles on this page.");
  }
}

async function sendToOffscreen(message) {
  const response = await chrome.runtime.sendMessage({ ...message, target: "offscreen" });
  if (!response?.ok) {
    throw new Error(response?.error || "The audio capture process did not respond.");
  }
  return response.result;
}

async function authorizeOffscreenMinute(minuteIndex) {
  await sendToOffscreen({
    type: "media:authorize-minute",
    sessionId: state.sessionId,
    minuteIndex,
    sessionStartedAt: state.sessionStartedAt,
  });
}

async function sendToTab(message) {
  if (!state.tabId) {
    return;
  }
  try {
    await chrome.tabs.sendMessage(state.tabId, message);
  } catch {
    // A full navigation destroys the old isolated world; onUpdated reinjects it.
  }
}

function publicState() {
  return {
    status: state.status,
    signedIn: state.signedIn,
    email: state.email,
    availableCredits: state.availableCredits,
    catalog: state.catalog,
    targetLanguage: state.targetLanguage,
    bilingualAvailable: state.bilingualAvailable,
    bilingualEnabled: state.bilingualEnabled,
    saveTranscript: state.saveTranscript,
    canSaveTranscript: canSaveTranscript(),
    effectiveCreditsPerMinute: effectiveCreditsPerMinute(),
    saveTranscriptHelp: saveTranscriptUnavailableMessage(),
    transcriptId: state.transcriptId,
    active: Boolean(state.sessionId),
    currentMinute: state.currentMinute,
    error: state.error,
    notice: state.notice,
  };
}

function canSaveTranscript() {
  return canSaveSourceTranscript(state.catalog, state.targetLanguage);
}

function effectiveCreditsPerMinute() {
  return getEffectiveCreditsPerMinute(state.catalog, state.saveTranscript);
}

function saveTranscriptUnavailableMessage() {
  const selected = state.catalog?.selectedQuizLanguage;
  if (state.catalog && !state.catalog.savedSourceTranscriptsEnabled) {
    return "Saved source transcripts are temporarily unavailable.";
  }
  if (!selected) {
    return "Choose one of Glosify’s four quiz languages before saving source speech.";
  }
  const target = state.catalog?.languages?.find(language => language.code === state.targetLanguage)?.name
    ?? state.targetLanguage;
  return selected.code === state.targetLanguage
    ? "Stores finalized original-language speech in your private Glosify account for this session only."
    : `Choose ${target} in Glosify, or change the subtitle language to ${selected.name}, before saving.`;
}

function broadcastState() {
  chrome.runtime.sendMessage({ target: "popup", type: "state:update", state: publicState() }).catch(() => {});
}

function statusText() {
  if (state.status === "reconnecting") {
    return "Reconnecting…";
  }
  return state.sessionId ? "Listening…" : "";
}

function isCapturableUrl(url) {
  try {
    const parsed = new URL(url);
    return parsed.protocol === "https:" || parsed.protocol === "http:";
  } catch {
    return false;
  }
}

function base64Url(bytes) {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

function normalizeError(error) {
  return error instanceof ApiRequestError
    ? error
    : new ApiRequestError(0, error?.message || "Unexpected extension error.");
}

class ApiRequestError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}
