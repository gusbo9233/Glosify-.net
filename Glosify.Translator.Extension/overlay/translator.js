import "../lib/translator-state.js";

// This document has the extension origin. Never put text, results or tokens in parent messages.
const State = globalThis.GlosifyTranslatorState;
const root = document;
const instanceId = location.hash.slice(1);
const parentOrigin = document.referrer ? new URL(document.referrer).origin : "*";

const elements = {
  window: root.querySelector(".window"),
  header: root.querySelector(".header"),
  minimize: root.querySelector(".minimize"),
  close: root.querySelector(".close"),
  sourceLanguage: root.querySelector("#source-language"),
  targetLanguage: root.querySelector("#target-language"),
  swap: root.querySelector(".swap"),
  sourceText: root.querySelector("#source-text"),
  preferences: root.querySelector("#preferences"),
  sourceCounter: root.querySelector(".source-counter"),
  preferenceCounter: root.querySelector(".preference-counter"),
  result: root.querySelector(".result"),
  saveDestination: root.querySelector(".save-destination"),
  saveLanguage: root.querySelector("#save-language"),
  translate: root.querySelector(".translate"),
  copy: root.querySelector(".copy"),
  save: root.querySelector(".save"),
  status: root.querySelector(".status"),
};
let port = null;
const SETTINGS_SAVE_DELAY_MS = 400;
let catalog = null;
let initialized = false;
let settingsSaveTimer = null;
let state = {
  sessionId: State.createRequestId(),
  sourceLanguage: "auto",
  targetLanguage: "en",
  preferences: "",
  sourceText: "",
  result: null,
  requestId: null,
  saveLanguage: null,
  saved: false,
  busy: false,
};

const instance = {
  focus() {
    setMinimized(false);
    elements.window.style.display = "flex";
    elements.sourceText.focus({ preventScroll: true });
  },
  initialize(message) {
    if (initialized) { instance.focus(); return; }
    catalog = message.catalog;
    const settings = State.normalizeSettings(message.settings, catalog);
    state = { ...state, ...settings };
    populateLanguages();
    elements.preferences.value = state.preferences;
    elements.sourceText.maxLength = catalog.maxSourceCharacters ?? 8000;
    elements.preferences.maxLength = catalog.maxPreferenceCharacters ?? 500;
    initialized = true;
    render();
    instance.focus();
  },
};
// The host may only request focus; no page messages can change input or invoke paid actions.
window.addEventListener("message", event => {
  if (event.source === parent && event.data?.type === "host:focus") instance.focus();
});
window.addEventListener("pagehide", () => port?.disconnect());
void bootstrap();

elements.close.addEventListener("click", closeOverlay);
elements.minimize.addEventListener("click", () => {
  setMinimized(!elements.window.classList.contains("minimized"));
});
elements.sourceLanguage.addEventListener("change", () => changed("immediate"));
elements.targetLanguage.addEventListener("change", () => changed("immediate"));
elements.sourceText.addEventListener("input", () => changed());
elements.preferences.addEventListener("input", () => changed("debounced"));
elements.swap.addEventListener("click", swapLanguages);
elements.translate.addEventListener("click", translate);
elements.copy.addEventListener("click", copyResult);
elements.saveLanguage.addEventListener("change", () => {
  if (!state.result || state.saved || state.busy) return;
  state.saveLanguage = elements.saveLanguage.value;
});
elements.save.addEventListener("click", saveResult);
installDragging();
render();

function setMinimized(minimized) {
  if (minimized === elements.window.classList.contains("minimized")) return;
  layout({ type: "frame:minimize", minimized });
  elements.window.classList.toggle("minimized", minimized);
  elements.minimize.textContent = minimized ? "+" : "−";
  elements.minimize.setAttribute("aria-label", minimized ? "Restore" : "Minimize");
}

function populateLanguages() {
  elements.sourceLanguage.replaceChildren(...catalog.sourceLanguages.map(language => option(language)));
  elements.targetLanguage.replaceChildren(...catalog.languages.map(language => option(language)));
  elements.sourceLanguage.value = state.sourceLanguage;
  elements.targetLanguage.value = state.targetLanguage;
}

function option(language) {
  const node = document.createElement("option");
  node.value = language.code;
  node.textContent = language.name;
  return node;
}

function changed(settingsPersistence = "none") {
  state = State.invalidate({
    ...state,
    sourceLanguage: elements.sourceLanguage.value,
    targetLanguage: elements.targetLanguage.value,
    sourceText: elements.sourceText.value,
    preferences: elements.preferences.value,
  });
  if (settingsPersistence === "immediate") persistSettings();
  if (settingsPersistence === "debounced") scheduleSettingsSave();
  clearStatus();
  render();
}

function scheduleSettingsSave() {
  if (settingsSaveTimer !== null) clearTimeout(settingsSaveTimer);
  settingsSaveTimer = setTimeout(persistSettings, SETTINGS_SAVE_DELAY_MS);
}

function persistSettings() {
  if (settingsSaveTimer !== null) clearTimeout(settingsSaveTimer);
  settingsSaveTimer = null;
  void sendMessage({
    type: "overlay:settings",
    settings: {
      sourceLanguage: state.sourceLanguage,
      targetLanguage: state.targetLanguage,
      preferences: state.preferences,
    },
  }).catch(error => setStatus(error.message, true));
}

function swapLanguages() {
  const swapped = State.swap(state);
  if (swapped === state) return;
  state = swapped;
  elements.sourceLanguage.value = state.sourceLanguage;
  elements.targetLanguage.value = state.targetLanguage;
  elements.sourceText.value = state.sourceText;
  changed("immediate");
}

async function translate() {
  if (!initialized || !State.canTranslate(state)) return;
  connectPort();
  state.busy = true;
  state.result = null;
  render();
  setStatus("Translating…");
  try {
    const response = await sendMessage({
      type: "overlay:translate",
      request: {
        sourceText: state.sourceText,
        sourceLanguage: state.sourceLanguage,
        targetLanguage: state.targetLanguage,
        preferences: state.preferences || null,
      },
    });
    if (!response?.ok) throw new Error(response?.error || "Translation failed.");
    state.result = response.result;
    state.requestId = State.createRequestId();
    state.saveLanguage = response.result.targetLanguage;
    state.saved = false;
    populateSaveLanguages();
    setStatus(`${response.result.remainingCredits} credits remaining.`);
  } catch (error) {
    setStatus(error?.message || "Translation failed.", true);
  } finally {
    state.busy = false;
    render();
  }
}

async function copyResult() {
  if (!state.result) return;
  try {
    await navigator.clipboard.writeText(state.result.translatedText);
    setStatus("Copied.");
  } catch {
    setStatus("Chrome could not copy the translation.", true);
  }
}

async function saveResult() {
  if (!state.result || !state.requestId || state.saved || state.busy) return;
  connectPort();
  state.busy = true;
  render();
  setStatus("Saving…");
  try {
    const response = await sendMessage({
      type: "overlay:save",
      request: {
        sessionId: state.sessionId,
        requestId: state.requestId,
        translationOperationId: state.result.translationOperationId,
        languageCode: state.saveLanguage,
        sourceLanguage: state.result.sourceLanguage,
        detectedSourceLanguage: state.result.detectedSourceLanguage,
        targetLanguage: state.result.targetLanguage,
        preferences: state.preferences || null,
        sourceText: state.sourceText,
        translatedText: state.result.translatedText,
      },
    });
    if (!response?.ok) throw new Error(response?.error || "Saving failed.");
    state.saved = true;
    setStatus("Saved to Glosify.");
  } catch (error) {
    setStatus(error?.message || "Saving failed.", true);
  } finally {
    state.busy = false;
    render();
  }
}

function render() {
  elements.sourceCounter.textContent = `${elements.sourceText.value.length} / ${catalog?.maxSourceCharacters ?? 8000}`;
  elements.preferenceCounter.textContent = `${elements.preferences.value.length} / ${catalog?.maxPreferenceCharacters ?? 500}`;
  const valid = initialized && State.canTranslate(state);
  elements.translate.disabled = !valid;
  elements.copy.disabled = !state.result || state.busy;
  elements.save.disabled = !state.result || !state.requestId || !state.saveLanguage || state.saved || state.busy;
  elements.save.textContent = state.saved ? "Saved" : "Save";
  elements.saveDestination.hidden = !state.result;
  elements.saveLanguage.disabled = !state.result || state.saved || state.busy;
  const effectiveSource = state.sourceLanguage === "auto"
    ? state.result?.detectedSourceLanguage
    : state.sourceLanguage;
  elements.swap.disabled = state.busy
    || !state.result
    || !effectiveSource
    || effectiveSource === state.targetLanguage;
  elements.sourceLanguage.disabled = state.busy;
  elements.targetLanguage.disabled = state.busy;
  elements.sourceText.disabled = state.busy;
  elements.preferences.disabled = state.busy;
  elements.result.textContent = state.result?.translatedText ?? "Your translation will appear here.";
  elements.result.classList.toggle("placeholder", !state.result);
}

function populateSaveLanguages() {
  const result = state.result;
  if (!result) {
    elements.saveLanguage.replaceChildren();
    return;
  }
  const effectiveSource = result.sourceLanguage === "auto"
    ? result.detectedSourceLanguage
    : result.sourceLanguage;
  const choices = [
    { code: result.targetLanguage, suffix: "translation" },
  ];
  if (effectiveSource && effectiveSource !== result.targetLanguage) {
    choices.push({ code: effectiveSource, suffix: "source" });
  }
  elements.saveLanguage.replaceChildren(...choices.map(choice => {
    const language = catalog.languages.find(item => item.code === choice.code);
    return option({
      code: choice.code,
      name: `${language?.name ?? choice.code.toUpperCase()} (${choice.suffix})`,
    });
  }));
  elements.saveLanguage.value = state.saveLanguage ?? result.targetLanguage;
}

function setStatus(text, error = false) {
  elements.status.textContent = text;
  elements.status.classList.toggle("error", error);
}

function clearStatus() {
  setStatus("");
}

function closeOverlay() {
  if (settingsSaveTimer !== null) persistSettings();
  sendMessage({ type: "overlay:closed" }).catch(() => {});
  port?.disconnect();
  layout({ type: "frame:close" });
}

function installDragging() {
  elements.header.addEventListener("pointerdown", event => {
    if (event.target.closest("button")) return;
    if (elements.header.hasPointerCapture(event.pointerId)) elements.header.releasePointerCapture(event.pointerId);
    layout({ type: "frame:drag-start", x: event.screenX, y: event.screenY });
  });
}

function layout(message) {
  parent.postMessage(message, parentOrigin);
}

function sendMessage(message) {
  return chrome.runtime.sendMessage({ ...message, instanceId });
}

function connectPort() {
  if (port) return;
  const connected = chrome.runtime.connect({ name: "translator-overlay" });
  port = connected;
  connected.onDisconnect.addListener(() => { if (port === connected) port = null; });
}

async function bootstrap() {
  try {
    const response = await sendMessage({ type: "overlay:bootstrap" });
    if (!response?.ok) throw new Error(response?.error || "Could not start the translator.");
    instance.initialize({ catalog: response.result.catalog, settings: response.result });
  } catch (error) {
    setStatus(error.message, true);
  }
}
