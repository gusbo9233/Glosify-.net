import { CONFIG } from "../config.js";
import "../lib/translator-state.js";

const TranslatorState = globalThis.GlosifyTranslatorState;
const STORAGE_KEYS = Object.freeze({
  refreshToken: "glosifyTranslatorRefreshToken",
  sourceLanguage: "glosifyTranslatorSourceLanguage",
  targetLanguage: "glosifyTranslatorTargetLanguage",
  preferences: "glosifyTranslatorPreferences",
});
const AUTHORIZATION_CLOCK_SKEW_MS = 30_000;
const REQUEST_TIMEOUT_MS = 210_000;
const REQUEST_KEEPALIVE_MS = 20_000;

let accessToken = null;
let accessExpiresAt = 0;
let refreshToken = null;
let refreshGeneration = 0;
// Unlike refreshGeneration, this changes only when the signed-in session changes.
let sessionGeneration = 0;
let refreshPromise = null;
const activeRequests = new Map();
const overlayPorts = new Map();
const state = {
  signedIn: false,
  status: "disconnected",
  email: null,
  availableCredits: null,
  catalog: null,
  sourceLanguage: "auto",
  targetLanguage: "en",
  preferences: "",
  error: null,
};

const initialization = restoreLocalState();

chrome.runtime.onConnect.addListener(port => {
  if (port.name !== "translator-overlay" || !isOverlaySender(port.sender)) return;
  const tabId = port.sender.tab.id;
  overlayPorts.set(tabId, port);
  port.onDisconnect.addListener(() => {
    if (overlayPorts.get(tabId) !== port) return;
    overlayPorts.delete(tabId);
    activeRequests.get(tabId)?.abort();
    activeRequests.delete(tabId);
  });
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.target === "popup") return false;
  handleMessage(message, sender)
    .then(result => sendResponse({ ok: true, result }))
    .catch(error => {
      const normalized = normalizeError(error);
      sendResponse({ ok: false, error: normalized.message, status: normalized.status, code: normalized.code });
    });
  return true;
});

async function handleMessage(message, sender) {
  await initialization;
  if (message?.type?.startsWith("overlay:")) await requireOverlay(message, sender);
  else if (sender?.url !== chrome.runtime.getURL("popup/popup.html")) {
    throw new Error("This message must come from the extension popup.");
  }
  switch (message?.type) {
    case "popup:get-state":
      if (refreshToken) await refreshAccountState();
      return publicState();
    case "popup:sign-in":
      await signIn();
      return publicState();
    case "popup:sign-out":
      await signOut();
      return publicState();
    case "popup:start":
      await startOverlay();
      return publicState();
    case "popup:open-saved":
      await chrome.tabs.create({ url: new URL("/Translations", CONFIG.glosifyBaseUrl).toString() });
      return publicState();
    case "overlay:settings":
      await saveSettings(message.settings);
      return publicState();
    case "overlay:bootstrap":
      if (!state.signedIn || !state.catalog) await refreshAccountState();
      if (!state.signedIn || !state.catalog) throw new Error(state.error || "Connect your Glosify account first.");
      return publicState();
    case "overlay:translate":
      return translate(sender.tab?.id, message.request);
    case "overlay:save":
      return saveTranslation(message.request);
    case "overlay:closed":
      activeRequests.get(sender.tab?.id)?.abort();
      activeRequests.delete(sender.tab?.id);
      return null;
    case "test:seed-auth":
      requireTestHooks();
      await chrome.storage.local.set({ [STORAGE_KEYS.refreshToken]: message.refreshToken });
      refreshToken = message.refreshToken;
      refreshGeneration += 1;
      sessionGeneration += 1;
      accessToken = null;
      accessExpiresAt = 0;
      state.signedIn = true;
      return publicState();
    default:
      return null;
  }
}

async function restoreLocalState() {
  await chrome.storage.local.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
  const stored = await chrome.storage.local.get(Object.values(STORAGE_KEYS));
  refreshToken = stored[STORAGE_KEYS.refreshToken] ?? null;
  refreshGeneration += 1;
  Object.assign(state, {
    signedIn: Boolean(refreshToken),
    status: refreshToken ? "ready" : "disconnected",
    sourceLanguage: stored[STORAGE_KEYS.sourceLanguage] ?? "auto",
    targetLanguage: stored[STORAGE_KEYS.targetLanguage] ?? "en",
    preferences: stored[STORAGE_KEYS.preferences] ?? "",
  });
}

async function signIn() {
  const generation = ++sessionGeneration;
  const codeVerifier = base64Url(crypto.getRandomValues(new Uint8Array(32)));
  const codeChallenge = base64Url(new Uint8Array(await crypto.subtle.digest(
    "SHA-256", new TextEncoder().encode(codeVerifier))));
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
    requireSession(generation);
    if (!callbackUrl) throw new Error("Glosify sign-in was cancelled.");
    const callback = new URL(callbackUrl);
    const expectedCallback = new URL(redirectUri);
    if (callback.origin !== expectedCallback.origin || callback.pathname !== expectedCallback.pathname
      || callback.username || callback.password || callback.hash) {
      throw new Error("Glosify sign-in returned an invalid callback URL.");
    }
    if (callback.searchParams.get("state") !== oauthState) {
      throw new Error("Glosify sign-in returned an invalid state value.");
    }
    const code = callback.searchParams.get("code");
    if (!code) throw new Error(callback.searchParams.get("error") || "Glosify sign-in did not return a code.");
    const response = await fetchResponse(new URL("/api/extension-auth/exchange", CONFIG.glosifyBaseUrl), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      cache: "no-store",
      body: JSON.stringify({ code, redirectUri, codeVerifier }),
    });
    if (!response.ok) throw await apiError(response);
    await acceptTokenResponse(await response.json(), generation);
    await refreshAccountState();
  } catch (error) {
    if (generation !== sessionGeneration) throw error;
    state.status = refreshToken ? "ready" : "disconnected";
    state.error = normalizeError(error).message;
    broadcastState();
    throw error;
  }
}

async function signOut() {
  sessionGeneration += 1;
  for (const controller of activeRequests.values()) controller.abort();
  activeRequests.clear();
  accessToken = null;
  accessExpiresAt = 0;
  refreshToken = null;
  refreshGeneration += 1;
  refreshPromise = null;
  Object.assign(state, {
    signedIn: false,
    status: "disconnected",
    email: null,
    availableCredits: null,
    catalog: null,
    error: null,
  });
  broadcastState();
  await chrome.storage.local.remove(STORAGE_KEYS.refreshToken);
}

async function acceptTokenResponse(tokens, generation) {
  requireSession(generation);
  if (!tokens?.accessToken || !tokens?.refreshToken) throw new Error("Glosify returned an invalid token response.");
  accessToken = tokens.accessToken;
  accessExpiresAt = Date.now() + Math.max(30_000, Number(tokens.expiresIn ?? 3600) * 1000 - AUTHORIZATION_CLOCK_SKEW_MS);
  refreshToken = tokens.refreshToken;
  refreshGeneration += 1;
  state.signedIn = true;
  await chrome.storage.local.set({ [STORAGE_KEYS.refreshToken]: refreshToken });
  requireSession(generation);
}

async function ensureAccessToken() {
  if (accessToken && Date.now() < accessExpiresAt) return accessToken;
  if (!refreshToken) throw new ApiRequestError(401, "Connect your Glosify account first.");
  if (!refreshPromise) {
    const usedToken = refreshToken;
    const usedGeneration = refreshGeneration;
    const generation = sessionGeneration;
    const pending = (async () => {
      const response = await fetchResponse(new URL("/api/auth/refresh", CONFIG.glosifyBaseUrl), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        cache: "no-store",
        body: JSON.stringify({ refreshToken: usedToken }),
      });
      requireSession(generation);
      if (!response.ok) {
        if (response.status === 401 || response.status === 403) {
          if (refreshGeneration === usedGeneration && refreshToken === usedToken) await clearExpiredAuthentication();
          throw new ApiRequestError(401, "Your Glosify session expired. Connect again.");
        }
        throw await apiError(response);
      }
      if (refreshGeneration !== usedGeneration || refreshToken !== usedToken) return accessToken;
      await acceptTokenResponse(await response.json(), generation);
      return accessToken;
    })().finally(() => { if (refreshPromise === pending) refreshPromise = null; });
    refreshPromise = pending;
  }
  return refreshPromise;
}

async function clearExpiredAuthentication() {
  await signOut();
}

async function apiFetch(path, options = {}, retryAuthentication = true) {
  const generation = sessionGeneration;
  const token = await ensureAccessToken();
  requireSession(generation);
  const headers = new Headers(options.headers ?? {});
  headers.set("Authorization", `Bearer ${token}`);
  if (options.body !== undefined && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  const response = await fetchResponse(new URL(path, CONFIG.glosifyBaseUrl), { ...options, headers, cache: "no-store" });
  requireSession(generation);
  if (response.status === 401 && retryAuthentication) {
    if (accessToken === token) {
      accessToken = null;
      accessExpiresAt = 0;
    }
    await ensureAccessToken();
    requireSession(generation);
    return apiFetch(path, options, false);
  }
  if (!response.ok) throw await apiError(response);
  if (response.status === 204) return null;
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

async function apiError(response) {
  try {
    return Object.assign(new ApiRequestError(response.status, ""), TranslatorState.parseProblem(await response.json(), response.status));
  } catch {
    return new ApiRequestError(response.status, `Glosify request failed (${response.status}).`);
  }
}

async function refreshAccountState() {
  const generation = sessionGeneration;
  try {
    const [me, catalog] = await Promise.all([
      apiFetch("/api/me"),
      apiFetch("/api/translator/catalog"),
    ]);
    requireSession(generation);
    if (typeof me?.email !== "string" || !me.email || !Number.isFinite(me.availableCredits)
      || !Array.isArray(catalog?.languages) || !catalog.languages.length
      || !Array.isArray(catalog.sourceLanguages) || !catalog.sourceLanguages.length) {
      throw new Error("Glosify returned incompatible account or Translator data. Check the server deployment.");
    }
    state.signedIn = true;
    state.status = "ready";
    state.email = me.email;
    state.availableCredits = me.availableCredits;
    state.catalog = catalog;
    Object.assign(state, TranslatorState.normalizeSettings(state, catalog));
    state.error = null;
    await persistSettings();
  } catch (error) {
    if (generation !== sessionGeneration) return;
    const normalized = normalizeError(error);
    if (normalized.status !== 401) {
      state.status = "error";
      state.error = normalized.message;
    }
  }
  broadcastState();
}

async function startOverlay() {
  if (!state.signedIn || !state.catalog) await refreshAccountState();
  if (!state.signedIn || !state.catalog) throw new Error(state.error || "Connect your Glosify account first.");
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id || !isWebUrl(tab.url)) throw new Error("Open a normal web page before starting the translator.");
  await chrome.scripting.executeScript({
    target: { tabId: tab.id },
    files: ["lib/translator-state.js", "content/translator.js"],
  });
  await chrome.tabs.sendMessage(tab.id, {
    type: "overlay:focus",
  }, { frameId: 0 });
}

function isOverlaySender(sender) {
  return sender?.id === chrome.runtime.id && sender.tab?.id !== undefined
    && sender.url?.split("#")[0] === chrome.runtime.getURL("overlay/translator.html");
}

async function requireOverlay(message, sender) {
  if (!isOverlaySender(sender) || !message.instanceId
    || new URL(sender.url).hash.slice(1) !== message.instanceId) {
    throw new Error("The translator frame is unavailable.");
  }
  // The content-script claim survives worker suspension and rejects unsolicited embedded frames.
  const claim = await chrome.tabs.sendMessage(sender.tab.id, {
    type: "host:claim", instanceId: message.instanceId, frameId: sender.frameId,
  }, { frameId: 0 });
  if (!claim?.matches) throw new Error("Open the translator from the extension popup.");
}

function requireSession(generation) {
  if (generation !== sessionGeneration) throw new ApiRequestError(401, "The Glosify session changed. Try again.");
}

async function fetchResponse(url, options = {}) {
  const timeout = new AbortController();
  const timeoutId = setTimeout(() => timeout.abort(new DOMException("Glosify request timed out.", "TimeoutError")), REQUEST_TIMEOUT_MS);
  // Chrome's documented long-operation keepalive. Never run this while the extension is idle.
  const keepAlive = setInterval(() => { chrome.runtime.getPlatformInfo().catch(() => {}); }, REQUEST_KEEPALIVE_MS);
  try {
    const signal = options.signal ? AbortSignal.any([options.signal, timeout.signal]) : timeout.signal;
    const response = await fetch(url, {
      ...options, signal,
      // API calls use only explicit bearer credentials. Never forward a refresh
      // token or submitted text through an unexpected server redirect.
      credentials: "omit", redirect: "error", referrerPolicy: "no-referrer",
    });
    // Keep the worker alive through the body as well as the response headers.
    const body = response.status === 204 ? null : await response.arrayBuffer();
    return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
  } finally {
    clearInterval(keepAlive);
    clearTimeout(timeoutId);
  }
}

async function saveSettings(settings) {
  Object.assign(state, TranslatorState.normalizeSettingsForStorage(settings, state.catalog));
  await persistSettings();
  broadcastState();
}

async function persistSettings() {
  await chrome.storage.local.set({
    [STORAGE_KEYS.sourceLanguage]: state.sourceLanguage,
    [STORAGE_KEYS.targetLanguage]: state.targetLanguage,
    [STORAGE_KEYS.preferences]: state.preferences,
  });
}

async function translate(tabId, request) {
  if (tabId === undefined) throw new Error("The translator tab is unavailable.");
  if (activeRequests.has(tabId)) throw new Error("A translation is already in progress.");
  const controller = new AbortController();
  activeRequests.set(tabId, controller);
  try {
    const result = await apiFetch("/api/translator/translate", {
      method: "POST",
      body: JSON.stringify(request),
      signal: controller.signal,
    });
    state.availableCredits = result.remainingCredits;
    state.error = null;
    broadcastState();
    return result;
  } finally {
    if (activeRequests.get(tabId) === controller) activeRequests.delete(tabId);
  }
}

async function saveTranslation(request) {
  return apiFetch("/api/translator/saved-translations", {
    method: "POST",
    body: JSON.stringify(request),
  });
}

function publicState() {
  return { ...state };
}

function broadcastState() {
  chrome.runtime.sendMessage({ target: "popup", type: "state:update", state: publicState() }).catch(() => {});
}

function isWebUrl(value) {
  try {
    return ["http:", "https:"].includes(new URL(value).protocol);
  } catch {
    return false;
  }
}

function base64Url(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

function requireTestHooks() {
  if (CONFIG.testHooksEnabled !== true) throw new Error("Test hooks are disabled.");
}

function normalizeError(error) {
  if (error instanceof ApiRequestError) return error;
  if (error?.name === "AbortError") return new ApiRequestError(0, "Translation cancelled.");
  return new ApiRequestError(0, error?.message || "Unexpected extension error.");
}

class ApiRequestError extends Error {
  constructor(status, message, code = null) {
    super(message);
    this.status = status;
    this.code = code;
  }
}
