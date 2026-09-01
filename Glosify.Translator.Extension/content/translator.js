(function installTranslatorOverlay() {
  const INSTANCE_KEY = "__glosifyTranslatorOverlay";
  if (globalThis[INSTANCE_KEY]) {
    globalThis[INSTANCE_KEY].focus();
    return;
  }

  const State = globalThis.GlosifyTranslatorState;
  const host = document.createElement("div");
  host.id = "glosify-translator-host";
  host.style.cssText = "all:initial;position:fixed;z-index:2147483647;inset:0 auto auto 0";
  document.documentElement.append(host);
  const root = host.attachShadow({ mode: "closed" });
  root.innerHTML = `
    <style>
      :host{all:initial}.window{position:fixed;top:72px;right:28px;width:410px;height:600px;min-width:320px;min-height:260px;max-width:calc(100vw - 16px);max-height:calc(100vh - 16px);resize:both;overflow:hidden;display:flex;flex-direction:column;color:#17233b;background:#fbfbfe;border:1px solid #d9dceb;border-radius:16px;box-shadow:0 18px 55px #1f244a38;font:13px/1.4 Inter,ui-sans-serif,system-ui,sans-serif}
      *{box-sizing:border-box}.header{display:flex;align-items:center;gap:9px;padding:11px 12px;background:#fff;border-bottom:1px solid #e4e6ee;cursor:move;touch-action:none;user-select:none}.mark{display:grid;place-items:center;width:28px;height:28px;border-radius:8px;background:#6558e8;color:#fff;font-weight:800}.title{font-weight:800;flex:1}.icon{width:30px;height:30px;padding:0;border:0;border-radius:8px;background:transparent;color:#606980;font-size:18px;cursor:pointer}.icon:hover{background:#f0f1f6}.body{display:flex;flex-direction:column;gap:10px;min-height:0;flex:1;padding:12px}.languages{display:grid;grid-template-columns:1fr 36px 1fr;align-items:end;gap:7px}.field{display:flex;flex-direction:column;gap:4px;min-width:0}label{font-size:11px;font-weight:750;color:#697288}select,textarea{width:100%;font:inherit;color:#17233b;background:#fff;border:1px solid #d9dcea;border-radius:9px;padding:8px;outline:0}select:focus,textarea:focus{border-color:#6558e8;box-shadow:0 0 0 2px #6558e81c}.swap{height:36px;padding:0;border:1px solid #d9dcea;border-radius:9px;background:#fff;cursor:pointer;font-size:18px}.swap:disabled{opacity:.4}.source{min-height:115px;resize:vertical}.preferences{min-height:53px;max-height:90px;resize:vertical}.result-wrap{display:flex;flex-direction:column;min-height:90px;flex:1}.result{white-space:pre-wrap;overflow:auto;min-height:70px;flex:1;padding:10px;border:1px solid #e1e3ec;border-radius:9px;background:#fff;color:#17233b;user-select:text}.placeholder{color:#8991a4}.actions{display:grid;grid-template-columns:1fr auto auto;gap:7px}.button{border:1px solid #d5d8e4;border-radius:9px;padding:9px 12px;background:#fff;color:#27324a;font:inherit;font-weight:750;cursor:pointer}.primary{background:#6558e8;border-color:#6558e8;color:#fff}.button:disabled{opacity:.45;cursor:default}.status{min-height:18px;font-size:12px;color:#667086}.status.error{color:#ad2828}.counter{font-size:10px;color:#8b92a2;text-align:right}.minimized{height:auto!important;min-height:0}.minimized .body{display:none}@media(max-width:500px){.window{left:8px!important;right:8px!important;top:8px!important;width:calc(100vw - 16px)}}
    </style>
    <section class="window" role="dialog" aria-label="Glosify Translator">
      <header class="header"><span class="mark">G</span><span class="title">Glosify Translator</span><button class="icon minimize" title="Minimize" aria-label="Minimize">−</button><button class="icon close" title="Close" aria-label="Close">×</button></header>
      <div class="body">
        <div class="languages">
          <div class="field"><label for="source-language">From</label><select id="source-language"></select></div>
          <button class="swap" title="Swap languages" aria-label="Swap languages">⇄</button>
          <div class="field"><label for="target-language">To</label><select id="target-language"></select></div>
        </div>
        <div class="field"><label for="source-text">Text</label><textarea class="source" id="source-text" placeholder="Type or paste text to translate" maxlength="8000"></textarea><span class="counter source-counter">0 / 8000</span></div>
        <div class="field"><label for="preferences">Optional preferences</label><textarea class="preferences" id="preferences" placeholder="For example: informal Mexican Spanish" maxlength="500"></textarea><span class="counter preference-counter">0 / 500</span></div>
        <div class="result-wrap"><label>Translation</label><div class="result placeholder" tabindex="0">Your translation will appear here.</div></div>
        <div class="actions"><button class="button primary translate">Translate</button><button class="button copy" disabled>Copy</button><button class="button save" disabled>Save</button></div>
        <div class="status" role="status" aria-live="polite"></div>
      </div>
    </section>`;

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
    translate: root.querySelector(".translate"),
    copy: root.querySelector(".copy"),
    save: root.querySelector(".save"),
    status: root.querySelector(".status"),
  };
  const port = chrome.runtime.connect({ name: "translator-overlay" });
  let catalog = null;
  let initialized = false;
  let testHooksEnabled = false;
  let state = {
    sourceLanguage: "auto",
    targetLanguage: "en",
    preferences: "",
    sourceText: "",
    result: null,
    requestId: null,
    saved: false,
    busy: false,
  };

  const instance = {
    focus() {
      elements.window.classList.remove("minimized");
      elements.window.style.display = "flex";
      elements.sourceText.focus();
    },
    initialize(message) {
      catalog = message.catalog;
      testHooksEnabled = message.testHooksEnabled === true;
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
  globalThis[INSTANCE_KEY] = instance;

  chrome.runtime.onMessage.addListener(message => {
    if (message?.type === "overlay:initialize") {
      instance.initialize(message);
      return Promise.resolve({ initialized: true });
    }
    if (message?.type === "overlay:focus") instance.focus();
    if (message?.type?.startsWith("test:overlay:")) {
      if (!testHooksEnabled) return Promise.reject(new Error("Translator test hooks are disabled."));
      return handleTestMessage(message);
    }
    return undefined;
  });

  elements.close.addEventListener("click", closeOverlay);
  elements.minimize.addEventListener("click", () => {
    const minimized = elements.window.classList.toggle("minimized");
    elements.minimize.textContent = minimized ? "+" : "−";
    elements.minimize.setAttribute("aria-label", minimized ? "Restore" : "Minimize");
  });
  elements.sourceLanguage.addEventListener("change", changed);
  elements.targetLanguage.addEventListener("change", changed);
  elements.sourceText.addEventListener("input", changed);
  elements.preferences.addEventListener("input", changed);
  elements.swap.addEventListener("click", swapLanguages);
  elements.translate.addEventListener("click", translate);
  elements.copy.addEventListener("click", copyResult);
  elements.save.addEventListener("click", saveResult);
  installDragging();
  render();

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

  function changed() {
    state = State.invalidate({
      ...state,
      sourceLanguage: elements.sourceLanguage.value,
      targetLanguage: elements.targetLanguage.value,
      sourceText: elements.sourceText.value,
      preferences: elements.preferences.value,
    });
    void chrome.runtime.sendMessage({
      type: "overlay:settings",
      settings: {
        sourceLanguage: state.sourceLanguage,
        targetLanguage: state.targetLanguage,
        preferences: state.preferences,
      },
    });
    clearStatus();
    render();
  }

  function swapLanguages() {
    state = State.swap(state);
    elements.sourceLanguage.value = state.sourceLanguage;
    elements.targetLanguage.value = state.targetLanguage;
    elements.sourceText.value = state.sourceText;
    changed();
  }

  async function translate() {
    if (!State.canTranslate(state)) return;
    state.busy = true;
    state.result = null;
    render();
    setStatus("Translating…");
    try {
      const response = await chrome.runtime.sendMessage({
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
      state.requestId = crypto.randomUUID();
      state.saved = false;
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
    if (!state.result || state.saved || state.busy) return;
    state.busy = true;
    render();
    setStatus("Saving…");
    try {
      const response = await chrome.runtime.sendMessage({
        type: "overlay:save",
        request: {
          requestId: state.requestId,
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
    elements.save.disabled = !state.result || state.saved || state.busy;
    elements.save.textContent = state.saved ? "Saved" : "Save";
    const canSwapAuto = state.sourceLanguage !== "auto" || Boolean(state.result?.detectedSourceLanguage);
    elements.swap.disabled = state.busy || !state.result || !canSwapAuto;
    elements.sourceLanguage.disabled = state.busy;
    elements.targetLanguage.disabled = state.busy;
    elements.sourceText.disabled = state.busy;
    elements.preferences.disabled = state.busy;
    elements.result.textContent = state.result?.translatedText ?? "Your translation will appear here.";
    elements.result.classList.toggle("placeholder", !state.result);
  }

  function setStatus(text, error = false) {
    elements.status.textContent = text;
    elements.status.classList.toggle("error", error);
  }

  function clearStatus() {
    setStatus("");
  }

  function closeOverlay() {
    chrome.runtime.sendMessage({ type: "overlay:closed" }).catch(() => {});
    port.disconnect();
    delete globalThis[INSTANCE_KEY];
    host.remove();
  }

  function installDragging() {
    let drag = null;
    elements.header.addEventListener("pointerdown", event => {
      if (event.target.closest("button")) return;
      const rect = elements.window.getBoundingClientRect();
      drag = { x: event.clientX, y: event.clientY, left: rect.left, top: rect.top };
      elements.window.style.left = `${rect.left}px`;
      elements.window.style.right = "auto";
      elements.header.setPointerCapture(event.pointerId);
    });
    elements.header.addEventListener("pointermove", event => {
      if (!drag) return;
      const maxLeft = Math.max(0, innerWidth - elements.window.offsetWidth);
      const maxTop = Math.max(0, innerHeight - 48);
      elements.window.style.left = `${Math.min(maxLeft, Math.max(0, drag.left + event.clientX - drag.x))}px`;
      elements.window.style.top = `${Math.min(maxTop, Math.max(0, drag.top + event.clientY - drag.y))}px`;
    });
    elements.header.addEventListener("pointerup", () => { drag = null; });
    elements.header.addEventListener("pointercancel", () => { drag = null; });
  }

  async function handleTestMessage(message) {
    switch (message.type) {
      case "test:overlay:state": {
        const rect = elements.window.getBoundingClientRect();
        return {
          sourceLanguage: state.sourceLanguage,
          targetLanguage: state.targetLanguage,
          sourceText: state.sourceText,
          preferences: state.preferences,
          translatedText: state.result?.translatedText ?? null,
          saved: state.saved,
          statusText: elements.status.textContent,
          statusError: elements.status.classList.contains("error"),
          minimized: elements.window.classList.contains("minimized"),
          rect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height },
        };
      }
      case "test:overlay:set-input":
        elements.sourceText.value = message.sourceText ?? elements.sourceText.value;
        elements.preferences.value = message.preferences ?? elements.preferences.value;
        if (message.sourceLanguage) elements.sourceLanguage.value = message.sourceLanguage;
        if (message.targetLanguage) elements.targetLanguage.value = message.targetLanguage;
        changed();
        return true;
      case "test:overlay:translate":
        await translate();
        return state.result;
      case "test:overlay:save":
        await saveResult();
        return state.saved;
      case "test:overlay:minimize":
        elements.minimize.click();
        return elements.window.classList.contains("minimized");
      case "test:overlay:move-resize":
        elements.window.style.left = `${message.left}px`;
        elements.window.style.right = "auto";
        elements.window.style.top = `${message.top}px`;
        elements.window.style.width = `${message.width}px`;
        elements.window.style.height = `${message.height}px`;
        return true;
      case "test:overlay:close":
        closeOverlay();
        return true;
      default:
        throw new Error("Unknown translator overlay test command.");
    }
  }
})();
