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

let accessToken = null;
let accessExpiresAt = 0;
let refreshToken = null;
let refreshGeneration = 0;
let refreshPromise = null;
const activeRequests = new Map();
const overlayPorts = new Map();
const state = {
  signedIn: false,
  status: "disconnected",
  email: null,
  availableCredits: 0,
  catalog: null,
  sourceLanguage: "auto",
  targetLanguage: "en",
  preferences: "",
  error: null,
};

const initialization = restoreLocalState();

chrome.runtime.onConnect.addListener(port => {
  if (port.name !== "translator-overlay" || port.sender?.tab?.id === undefined) return;
  const tabId = port.sender.tab.id;
  overlayPorts.set(tabId, port);
  port.onDisconnect.addListener(() => {
    if (overlayPorts.get(tabId) === port) overlayPorts.delete(tabId);
    activeRequests.get(tabId)?.abort();
    activeRequests.delete(tabId);
  });
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
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
    if (!callbackUrl) throw new Error("Glosify sign-in was cancelled.");
    const callback = new URL(callbackUrl);
    if (callback.searchParams.get("state") !== oauthState) {
      throw new Error("Glosify sign-in returned an invalid state value.");
    }
    const code = callback.searchParams.get("code");
    if (!code) throw new Error(callback.searchParams.get("error") || "Glosify sign-in did not return a code.");
    const response = await fetch(new URL("/api/extension-auth/exchange", CONFIG.glosifyBaseUrl), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      cache: "no-store",
      body: JSON.stringify({ code, redirectUri, codeVerifier }),
    });
    if (!response.ok) throw await apiError(response);
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
  for (const controller of activeRequests.values()) controller.abort();
  activeRequests.clear();
  accessToken = null;
  accessExpiresAt = 0;
  refreshToken = null;
  refreshGeneration += 1;
  await chrome.storage.local.remove(STORAGE_KEYS.refreshToken);
  Object.assign(state, {
    signedIn: false,
    status: "disconnected",
    email: null,
    availableCredits: 0,
    catalog: null,
    error: null,
  });
  broadcastState();
}

async function acceptTokenResponse(tokens) {
  if (!tokens?.accessToken || !tokens?.refreshToken) throw new Error("Glosify returned an invalid token response.");
  accessToken = tokens.accessToken;
  accessExpiresAt = Date.now() + Math.max(30_000, Number(tokens.expiresIn ?? 3600) * 1000 - AUTHORIZATION_CLOCK_SKEW_MS);
  refreshToken = tokens.refreshToken;
  refreshGeneration += 1;
  await chrome.storage.local.set({ [STORAGE_KEYS.refreshToken]: refreshToken });
  state.signedIn = true;
}

async function ensureAccessToken() {
  if (accessToken && Date.now() < accessExpiresAt) return accessToken;
  if (!refreshToken) throw new ApiRequestError(401, "Connect your Glosify account first.");
  if (!refreshPromise) {
    const usedToken = refreshToken;
    const usedGeneration = refreshGeneration;
    refreshPromise = (async () => {
      const response = await fetch(new URL("/api/auth/refresh", CONFIG.glosifyBaseUrl), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        cache: "no-store",
        body: JSON.stringify({ refreshToken: usedToken }),
      });
      if (!response.ok) {
        if (refreshGeneration === usedGeneration && refreshToken === usedToken) await clearExpiredAuthentication();
        throw new ApiRequestError(401, "Your Glosify session expired. Connect again.");
      }
      if (refreshGeneration !== usedGeneration || refreshToken !== usedToken) return accessToken;
      await acceptTokenResponse(await response.json());
      return accessToken;
    })().finally(() => { refreshPromise = null; });
  }
  return refreshPromise;
}

async function clearExpiredAuthentication() {
  accessToken = null;
  accessExpiresAt = 0;
  refreshToken = null;
  refreshGeneration += 1;
  await chrome.storage.local.remove(STORAGE_KEYS.refreshToken);
  state.signedIn = false;
  state.status = "disconnected";
  state.catalog = null;
  broadcastState();
}

async function apiFetch(path, options = {}, retryAuthentication = true) {
  const token = await ensureAccessToken();
  const headers = new Headers(options.headers ?? {});
  headers.set("Authorization", `Bearer ${token}`);
  if (options.body !== undefined && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  const response = await fetch(new URL(path, CONFIG.glosifyBaseUrl), { ...options, headers, cache: "no-store" });
  if (response.status === 401 && retryAuthentication) {
    if (accessToken === token) {
      accessToken = null;
      accessExpiresAt = 0;
    }
    await ensureAccessToken();
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
  try {
    const [me, catalog] = await Promise.all([
      apiFetch("/api/me"),
      apiFetch("/api/translator/catalog"),
    ]);
    state.signedIn = true;
    state.status = "ready";
    state.email = me.email;
    state.availableCredits = me.availableCredits;
    state.catalog = catalog;
    Object.assign(state, TranslatorState.normalizeSettings(state, catalog));
    await persistSettings();
    state.error = null;
  } catch (error) {
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
    type: "overlay:initialize",
    catalog: state.catalog,
    testHooksEnabled: CONFIG.testHooksEnabled === true,
    settings: {
      sourceLanguage: state.sourceLanguage,
      targetLanguage: state.targetLanguage,
      preferences: state.preferences,
    },
  });
}

async function saveSettings(settings) {
  if (!state.catalog) return;
  Object.assign(state, TranslatorState.normalizeSettings(settings, state.catalog));
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
