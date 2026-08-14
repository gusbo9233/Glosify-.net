import { CONFIG } from "../config.js";
import { getBillingAction } from "../lib/billing.js";
import {
  buildTranscriptSessionRequest,
  canSaveSourceTranscript,
  clearTranscriptStorageState,
  getEffectiveCreditsPerMinute,
  selectAvailableTranslationMode,
} from "../lib/transcript-storage.js";

const STORAGE_KEYS = Object.freeze({
  refreshToken: "glosifyRefreshToken",
  targetLanguage: "glosifyTargetLanguage",
  translationMode: "glosifyTranslationMode",
  sourceLanguage: "glosifySourceLanguage",
});

let accessToken = null;
let accessExpiresAt = 0;
let refreshToken = null;
let billingBusy = false;
let heartbeatBusy = false;
let relaySwitchBusy = false;
let paidStatusBusy = false;
let stopping = false;

const state = {
  status: "disconnected",
  signedIn: false,
  email: null,
  availableCredits: 0,
  catalog: null,
  targetLanguage: "en",
  translationMode: "scribe",
  sourceLanguage: "auto",
  saveTranscript: false,
  tabId: null,
  sessionId: null,
  transcriptId: null,
  currentMinute: 0,
  nextMinuteReserved: false,
  stopAtBoundary: false,
  stopAtBoundaryReason: null,
  sessionStartedAt: 0,
  connectionStartedAt: 0,
  lastHeartbeatAt: 0,
  firstCaptionLatencyMs: null,
  firstCaptionReported: false,
  reconnectReported: false,
  error: null,
  notice: null,
  paidServicesAvailable: true,
  paidServicesReason: null,
  paidServicesResetAtUtc: null,
  lastPaidStatusAt: 0,
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
    case "popup:set-mode":
      await setTranslationMode(message.translationMode);
      return publicState();
    case "popup:set-source":
      await setSourceLanguage(message.sourceLanguage);
      return publicState();
    case "popup:set-quiz-language":
      await setQuizLanguage(message.code);
      return publicState();
    case "popup:set-save-transcript":
      await setSaveTranscript(Boolean(message.enabled), message.quizLanguageCode);
      return publicState();
    case "popup:open-transcripts":
      await chrome.tabs.create({ url: new URL("/Transcripts", CONFIG.glosifyBaseUrl).toString() });
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
        await stopSession(message.error || "The selected subtitle provider connection ended.", "error");
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
  state.translationMode = stored[STORAGE_KEYS.translationMode] ?? "scribe";
  state.sourceLanguage = stored[STORAGE_KEYS.sourceLanguage] ?? "auto";
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
    paidServicesAvailable: true,
    paidServicesReason: null,
    paidServicesResetAtUtc: null,
    lastPaidStatusAt: 0,
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
  let code = null;
  let resetsAtUtc = null;
  try {
    const body = await response.json();
    message = body.detail ?? body.title ?? body.error ?? message;
    code = body.code ?? null;
    resetsAtUtc = body.resetsAtUtc ?? null;
  } catch {
    // Do not surface raw upstream responses; they can contain implementation details.
  }
  return new ApiRequestError(response.status, message, code, resetsAtUtc);
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
    await refreshPaidServiceStatus();
    if (!catalog.languages.some(language => language.code === state.targetLanguage)) {
      state.targetLanguage = catalog.languages[0]?.code ?? "en";
      await chrome.storage.local.set({ [STORAGE_KEYS.targetLanguage]: state.targetLanguage });
    }
    const modes = catalog.modes ?? [{ code: "enhanced", creditsPerMinute: catalog.creditsPerMinute ?? 8 }];
    if (!modes.some(mode => mode.code === state.translationMode)) {
      state.translationMode = selectAvailableTranslationMode(modes, state.translationMode);
      state.notice = `Your saved subtitle mode is unavailable; ${modes.find(mode => mode.code === state.translationMode)?.name ?? "an available mode"} is selected.`;
      await chrome.storage.local.set({ [STORAGE_KEYS.translationMode]: state.translationMode });
    }
    const sourceLanguages = catalog.sourceLanguages ?? [];
    if (!sourceLanguages.some(language => language.code === state.sourceLanguage)) {
      state.sourceLanguage = "auto";
      await chrome.storage.local.set({ [STORAGE_KEYS.sourceLanguage]: "auto" });
    }
    if (!state.sessionId
        && state.saveTranscript
        && !state.catalog.savedSourceTranscriptsEnabled) {
      state.saveTranscript = false;
      state.transcriptId = null;
    }
    if (!state.sessionId) {
      state.status = state.paidServicesAvailable ? "ready" : "budget_exhausted";
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
  await chrome.storage.local.set({ [STORAGE_KEYS.targetLanguage]: targetLanguage });
  broadcastState();
}

async function setTranslationMode(translationMode) {
  if (state.sessionId) {
    throw new Error("Stop the current subtitles before changing mode.");
  }
  const modes = state.catalog?.modes ?? [{ code: "enhanced" }];
  if (!modes.some(mode => mode.code === translationMode)) {
    throw new Error("Choose an available subtitle mode.");
  }
  state.translationMode = translationMode;
  await chrome.storage.local.set({ [STORAGE_KEYS.translationMode]: translationMode });
  broadcastState();
}

async function setSourceLanguage(sourceLanguage) {
  if (state.sessionId) {
    throw new Error("Stop the current subtitles before changing the spoken-language hint.");
  }
  if (!state.catalog?.sourceLanguages?.some(language => language.code === sourceLanguage)) {
    throw new Error("Choose a supported spoken-language hint.");
  }
  state.sourceLanguage = sourceLanguage;
  await chrome.storage.local.set({ [STORAGE_KEYS.sourceLanguage]: sourceLanguage });
  broadcastState();
}

async function setQuizLanguage(code) {
  if (state.sessionId) {
    throw new Error("Stop the current subtitles before changing quiz language.");
  }
  if (!state.catalog?.quizLanguages?.some(language => language.code === code)) {
    throw new Error("Choose Estonian, German, Polish, or Ukrainian.");
  }
  const selected = await apiFetch("/api/realtime-translation/quiz-language", {
    method: "PUT",
    body: JSON.stringify({ code }),
  });
  state.catalog = { ...state.catalog, selectedQuizLanguage: selected };
  if (state.catalog.languages.some(language => language.code === selected.code)) {
    state.targetLanguage = selected.code;
    await chrome.storage.local.set({ [STORAGE_KEYS.targetLanguage]: selected.code });
  }
  broadcastState();
}

async function setSaveTranscript(enabled, requestedQuizLanguageCode) {
  if (relaySwitchBusy) {
    throw new Error("Glosify is already changing subtitle mode.");
  }
  if (enabled === state.saveTranscript) {
    return;
  }
  relaySwitchBusy = true;
  try {
    if (enabled) {
      await ensureTranscriptLearningLanguage(requestedQuizLanguageCode);
    }

    const previousValue = state.saveTranscript;
    state.saveTranscript = enabled;
    state.error = null;
    if (state.sessionId) {
      const requiredCredits = effectiveCreditsPerMinute();
      if (state.availableCredits < requiredCredits) {
        state.saveTranscript = previousValue;
        throw new ApiRequestError(
          402,
          `You need ${requiredCredits} credits to change subtitle mode.`);
      }
      try {
        await reconnectRelaySession({
          connectingMessage: enabled
            ? "Starting saved transcript…"
            : "Stopping transcript saving…",
          completedNotice: enabled
            ? `Original speech is now being saved. The relay started a new ${requiredCredits}-credit minute.`
            : `Transcript saving stopped. The relay started a new ${requiredCredits}-credit minute.`,
        });
      } catch (error) {
        state.saveTranscript = previousValue;
        const normalized = normalizeError(error);
        await stopSession(normalized.message, normalized.status === 402 ? "insufficient_credits" : "error");
        throw error;
      }
    }
    broadcastState();
  } finally {
    relaySwitchBusy = false;
  }
}

async function ensureTranscriptLearningLanguage(requestedCode) {
  if (!state.catalog?.savedSourceTranscriptsEnabled) {
    throw new Error("Saved source transcripts are temporarily unavailable.");
  }
  const quizLanguages = state.catalog.quizLanguages ?? [];
  const requested = quizLanguages.find(language => language.code === requestedCode);
  const selected = state.catalog.selectedQuizLanguage;
  const matchingTarget = quizLanguages.find(language => language.code === state.targetLanguage);
  const learningLanguage = requested ?? selected ?? matchingTarget ?? quizLanguages[0];
  if (!learningLanguage) {
    throw new Error("Glosify did not return a supported quiz language.");
  }
  if (selected?.code === learningLanguage.code) {
    return;
  }
  const persisted = await apiFetch("/api/realtime-translation/quiz-language", {
    method: "PUT",
    body: JSON.stringify({ code: learningLanguage.code }),
  });
  state.catalog = { ...state.catalog, selectedQuizLanguage: persisted };
}

async function startSession() {
  if (state.sessionId || state.status === "connecting") {
    return;
  }
  await refreshAccountState();
  if (!state.signedIn || !state.catalog) {
    throw new Error(state.error || "Connect your Glosify account first.");
  }
  if (!state.paidServicesAvailable) {
    throw new ApiRequestError(
      503,
      budgetUnavailableMessage(),
      "paid_services_budget_exhausted",
      state.paidServicesResetAtUtc);
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
    state.stopAtBoundaryReason = null;
    state.sessionStartedAt = Date.now();
    state.lastHeartbeatAt = Date.now();
    state.status = "subtitling";
    state.notice = null;
    await authorizeOffscreenMinute(1);
    await sendToTab({ type: "overlay:status", text: "Listening…" });
    broadcastState();
  } catch (error) {
    const normalized = normalizeError(error);
    const budgetClosed = rememberBudgetClosure(normalized);
    await stopSession(
      budgetClosed ? budgetUnavailableMessage(normalized.resetsAtUtc) : normalized.message,
      budgetClosed ? "budget_exhausted" : normalized.status === 402 ? "insufficient_credits" : "error");
    throw error;
  }
}

async function handleSubtitleEvent(event) {
  if (!event || event.sessionId !== state.sessionId || !state.tabId) {
    return;
  }
  if (event.stream === "translation" && event.delta && state.firstCaptionLatencyMs === null) {
    state.firstCaptionLatencyMs = Math.max(0, Date.now() - state.connectionStartedAt);
  }
  await sendToTab({
    type: "overlay:subtitle",
    event,
  });
}

async function processTick() {
  if (!state.sessionId
      || !state.sessionStartedAt
      || stopping
      || relaySwitchBusy
      || state.status === "reconnecting") {
    return;
  }

  const now = Date.now();
  if (!paidStatusBusy && now - state.lastPaidStatusAt >= 15_000) {
    paidStatusBusy = true;
    try {
      await refreshPaidServiceStatus();
      if (!state.paidServicesAvailable) {
        state.stopAtBoundary = true;
        state.stopAtBoundaryReason = "budget";
        state.notice = `${budgetUnavailableMessage()} Subtitles will stop at the end of this paid minute.`;
        await sendToTab({ type: "overlay:status", text: state.notice });
        broadcastState();
      }
    } catch {
      // The minute reservation is still the authoritative fail-closed check.
    } finally {
      paidStatusBusy = false;
    }
  }
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
        const normalized = normalizeError(error);
        if (normalized.status === 402 || normalized.code === "paid_services_budget_exhausted") {
          rememberBudgetClosure(normalized);
          state.stopAtBoundary = true;
          state.stopAtBoundaryReason = normalized.status === 402 ? "credits" : "budget";
          state.notice = normalized.status === 402
            ? "Subtitles will stop at the end of this paid minute: not enough credits."
            : `${budgetUnavailableMessage(normalized.resetsAtUtc)} Subtitles will stop at the end of this paid minute.`;
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
      const budgetStopped = state.stopAtBoundaryReason === "budget";
      await stopSession(
        budgetStopped
          ? budgetUnavailableMessage()
          : state.stopAtBoundary
            ? "Subtitles stopped because your Glosify credits ran out."
            : "The next minute was not authorized.",
        budgetStopped ? "budget_exhausted" : state.stopAtBoundary ? "insufficient_credits" : "error");
    } else if (action.type === "reconnect") {
      await reconnectRelaySession();
    }
  } catch (error) {
    const normalized = normalizeError(error);
    const budgetClosed = rememberBudgetClosure(normalized);
    await stopSession(
      budgetClosed
        ? budgetUnavailableMessage(normalized.resetsAtUtc)
        : normalized.status === 402
        ? "Subtitles stopped because your Glosify credits ran out."
        : normalized.message,
      budgetClosed ? "budget_exhausted" : normalized.status === 402 ? "insufficient_credits" : "error");
  } finally {
    billingBusy = false;
  }
}

async function reconnectRelaySession({
  connectingMessage = "Reconnecting…",
  completedNotice = null,
} = {}) {
  const oldSessionId = state.sessionId;
  if (!oldSessionId) {
    return;
  }
  state.status = "reconnecting";
  state.notice = connectingMessage;
  broadcastState();
  await sendToTab({ type: "overlay:status", text: connectingMessage });
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
    notice: completedNotice,
  });
  await authorizeOffscreenMinute(1);
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
      stopAtBoundaryReason: null,
      sessionStartedAt: 0,
      connectionStartedAt: 0,
      lastHeartbeatAt: 0,
      firstCaptionLatencyMs: null,
      firstCaptionReported: false,
      reconnectReported: false,
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
    justification: "Capture tab audio, keep it audible locally, and stream it through Glosify for live subtitles.",
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
    translationMode: state.translationMode,
    sourceLanguage: state.sourceLanguage,
    saveTranscript: state.saveTranscript,
    canSaveTranscript: canSaveTranscript(),
    effectiveCreditsPerMinute: effectiveCreditsPerMinute(),
    saveTranscriptHelp: saveTranscriptUnavailableMessage(),
    transcriptId: state.transcriptId,
    active: Boolean(state.sessionId),
    currentMinute: state.currentMinute,
    error: state.error,
    notice: state.notice,
    paidServicesAvailable: state.paidServicesAvailable,
    paidServicesReason: state.paidServicesReason,
    paidServicesResetAtUtc: state.paidServicesResetAtUtc,
  };
}

function canSaveTranscript() {
  return canSaveSourceTranscript(state.catalog);
}

function effectiveCreditsPerMinute() {
  return getEffectiveCreditsPerMinute(
    state.catalog,
    state.saveTranscript,
    state.translationMode);
}

function saveTranscriptUnavailableMessage() {
  const selected = state.catalog?.selectedQuizLanguage;
  const speechHint = state.sourceLanguage === "auto"
    ? "automatic spoken-language detection"
    : `${state.catalog?.sourceLanguages?.find(language => language.code === state.sourceLanguage)?.name ?? state.sourceLanguage} as a spoken-language hint`;
  if (state.translationMode === "scribe") {
    return state.saveTranscript
      ? `Scribe’s finalized original speech is being saved using ${speechHint}.`
      : `Save Scribe’s finalized original speech using ${speechHint} at no extra credits per minute.`;
  }
  if (state.sessionId) {
    return state.saveTranscript
      ? `Scribe is saving finalized original speech using ${speechHint}. Uncheck anytime to stop saving.`
      : `Check anytime to use Scribe with ${speechHint} and save the original speech without stopping tab audio.`;
  }
  if (state.catalog && !state.catalog.savedSourceTranscriptsEnabled) {
    return "Saved source transcripts are temporarily unavailable.";
  }
  return selected
    ? `Check anytime to use Scribe with ${speechHint} and save the original speech for your ${selected.name} learning context.`
    : "Check anytime to save. Glosify will use the quiz language shown above.";
}

async function refreshPaidServiceStatus() {
  const status = await apiFetch("/api/service-status/paid-features");
  state.paidServicesAvailable = status.available !== false;
  state.paidServicesReason = status.reason ?? null;
  state.paidServicesResetAtUtc = status.resetsAtUtc ?? null;
  state.lastPaidStatusAt = Date.now();
  return status;
}

function budgetUnavailableMessage(resetOverride = null) {
  const value = resetOverride ?? state.paidServicesResetAtUtc;
  const reset = value ? new Date(value) : null;
  const suffix = reset && !Number.isNaN(reset.valueOf())
    ? ` They reopen ${reset.toLocaleString([], { dateStyle: "medium", timeStyle: "short" })}.`
    : " They reopen at the start of next month.";
  return `${state.paidServicesReason ?? "Glosify's monthly paid-services budget has been reached."}${suffix}`;
}

function rememberBudgetClosure(error) {
  if (error?.code !== "paid_services_budget_exhausted") {
    return false;
  }
  state.paidServicesAvailable = false;
  state.paidServicesReason = error.message || state.paidServicesReason;
  state.paidServicesResetAtUtc = error.resetsAtUtc ?? state.paidServicesResetAtUtc;
  state.lastPaidStatusAt = Date.now();
  return true;
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
  constructor(status, message, code = null, resetsAtUtc = null) {
    super(message);
    this.status = status;
    this.code = code;
    this.resetsAtUtc = resetsAtUtc;
  }
}
