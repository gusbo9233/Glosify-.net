(function installTranslatorOverlay() {
  const INSTANCE_KEY = "__glosifyTranslatorOverlay";
  if (globalThis[INSTANCE_KEY]) {
    globalThis[INSTANCE_KEY].focus();
    return;
  }

  const State = globalThis.GlosifyTranslatorState;
  const host = document.createElement("iframe");
  host.id = "glosify-translator-host";
  host.title = "Glosify Translator";
  host.style.cssText = "all:initial;position:fixed;z-index:2147483647;top:72px;right:28px;width:430px;height:625px;min-width:330px;min-height:280px;max-width:calc(100vw - 16px);max-height:calc(100vh - 16px);resize:both;overflow:auto;border:0;border-radius:20px;background:transparent;color-scheme:dark";
  if (innerWidth <= 500) {
    host.style.left = "8px";
    host.style.right = "8px";
    host.style.top = "8px";
    host.style.width = "calc(100vw - 16px)";
  }
  document.documentElement.append(host);
  const frameDocument = host.contentDocument;
  if (!frameDocument) {
    host.remove();
    throw new Error("Glosify Translator could not create its isolated frame.");
  }
  frameDocument.documentElement.style.cssText = "margin:0;width:100%;height:100%;overflow:hidden;background:transparent";
  frameDocument.body.style.cssText = "margin:0;width:100%;height:100%;overflow:hidden;background:transparent";
  const surface = frameDocument.createElement("div");
  surface.style.cssText = "all:initial;position:fixed;inset:0";
  frameDocument.body.append(surface);
  const root = surface.attachShadow({ mode: "closed" });
  root.innerHTML = `
    <style>
      :host{all:initial;--primary:#53e076;--primary-strong:#72fe8f;--on-primary:#003914;--background:#041329;--surface-lowest:#010e24;--surface-low:#0d1c32;--surface:#112036;--surface-high:#1c2a41;--surface-highest:#27354c;--on-surface:#d6e3ff;--on-surface-variant:#bccbb9;color-scheme:dark}
      *{box-sizing:border-box}
      .window{position:fixed;inset:0;width:100%;height:100%;overflow:hidden;display:flex;flex-direction:column;color:var(--on-surface);background:radial-gradient(circle at 92% 2%,rgba(83,224,118,.12),transparent 31%),linear-gradient(155deg,rgba(28,42,65,.96),rgba(4,19,41,.98) 54%),var(--background);border:1px solid rgba(83,224,118,.24);border-radius:20px;box-shadow:0 1px 0 rgba(255,255,255,.05) inset,0 28px 80px rgba(1,10,28,.52),0 0 42px rgba(83,224,118,.08);font:13px/1.45 "Plus Jakarta Sans",Inter,ui-sans-serif,system-ui,sans-serif}
      .window::before{content:"";position:absolute;z-index:2;inset:0 22px auto;height:1px;background:linear-gradient(90deg,transparent,var(--primary),transparent);opacity:.56;pointer-events:none}
      .header{display:flex;align-items:center;gap:10px;min-height:58px;padding:10px 12px 10px 14px;background:rgba(1,14,36,.64);border-bottom:1px solid rgba(214,227,255,.1);backdrop-filter:blur(18px);-webkit-backdrop-filter:blur(18px);cursor:move;touch-action:none;user-select:none}
      .mark{display:grid;place-items:center;flex:0 0 32px;width:32px;height:32px;border:1px solid rgba(114,254,143,.38);border-radius:11px;background:linear-gradient(145deg,var(--primary-strong),#1db954);color:var(--on-primary);box-shadow:0 0 20px rgba(83,224,118,.2);font-size:14px;font-style:italic;font-weight:900}
      .title{display:grid;flex:1;min-width:0;line-height:1}.wordmark{color:var(--primary);font-size:14px;font-style:italic;font-weight:900;letter-spacing:-.03em}.product{margin-top:4px;color:rgba(214,227,255,.54);font-size:8px;font-weight:800;letter-spacing:.16em;text-transform:uppercase}
      .beta{margin-right:3px;padding:4px 7px;border:1px solid rgba(83,224,118,.18);border-radius:999px;background:rgba(83,224,118,.07);color:var(--primary);font-size:8px;font-weight:850;letter-spacing:.12em;text-transform:uppercase}
      .icon{display:grid;place-items:center;width:30px;height:30px;padding:0;border:1px solid transparent;border-radius:9px;background:transparent;color:rgba(214,227,255,.6);font:18px/1 system-ui,sans-serif;cursor:pointer;transition:background 150ms ease,border-color 150ms ease,color 150ms ease}
      .icon:hover,.icon:focus-visible{border-color:rgba(214,227,255,.1);background:rgba(214,227,255,.07);color:var(--on-surface);outline:0}.close:hover,.close:focus-visible{border-color:rgba(255,180,171,.18);background:rgba(147,0,10,.2);color:#ffb4ab}
      .body{display:flex;flex:1;flex-direction:column;gap:11px;min-height:0;overflow:auto;padding:15px;scrollbar-color:rgba(83,224,118,.28) transparent;scrollbar-width:thin}
      .languages{display:grid;grid-template-columns:minmax(0,1fr) 38px minmax(0,1fr);align-items:end;gap:8px}.field{display:flex;flex-direction:column;gap:5px;min-width:0}
      label{color:rgba(214,227,255,.58);font-size:9px;font-weight:850;letter-spacing:.12em;text-transform:uppercase}
      select,textarea{width:100%;border:1px solid rgba(214,227,255,.12);border-radius:12px;outline:0;background:rgba(1,14,36,.62);color:var(--on-surface);font:inherit;transition:border-color 150ms ease,background 150ms ease,box-shadow 150ms ease}
      select{height:39px;padding:8px 10px;cursor:pointer}textarea{padding:10px 11px;line-height:1.5}textarea::placeholder{color:rgba(214,227,255,.33)}
      select:hover,textarea:hover{border-color:rgba(214,227,255,.2);background:rgba(13,28,50,.82)}select:focus,textarea:focus{border-color:var(--primary);background:rgba(13,28,50,.94);box-shadow:0 0 0 3px rgba(83,224,118,.12)}
      .swap{display:grid;place-items:center;width:38px;height:39px;padding:0;border:1px solid rgba(83,224,118,.2);border-radius:12px;background:rgba(83,224,118,.08);color:var(--primary);font:18px/1 system-ui,sans-serif;cursor:pointer;transition:background 150ms ease,border-color 150ms ease,transform 150ms ease}.swap:hover:not(:disabled),.swap:focus-visible:not(:disabled){border-color:rgba(83,224,118,.46);background:rgba(83,224,118,.14);outline:0;transform:translateY(-1px)}.swap:disabled{opacity:.38;cursor:default}
      .source{min-height:112px;resize:vertical}.preferences{min-height:55px;max-height:95px;resize:vertical}.counter{margin-top:-1px;color:rgba(214,227,255,.34);font-size:9px;font-variant-numeric:tabular-nums;text-align:right}
      .result-wrap{display:flex;flex:1;flex-direction:column;gap:5px;min-height:100px}.result{position:relative;flex:1;min-height:76px;overflow:auto;padding:11px;border:1px solid rgba(83,224,118,.14);border-radius:13px;background:radial-gradient(circle at 100% 0,rgba(83,224,118,.06),transparent 40%),rgba(1,14,36,.72);color:var(--on-surface);line-height:1.55;white-space:pre-wrap;user-select:text;scrollbar-color:rgba(83,224,118,.28) transparent;scrollbar-width:thin}.result:focus-visible{border-color:rgba(83,224,118,.46);outline:0}.result.placeholder{color:rgba(214,227,255,.33)}
      .save-destination{display:grid;grid-template-columns:auto minmax(0,1fr);align-items:center;gap:10px;padding:8px 10px;border:1px solid rgba(83,224,118,.14);border-radius:12px;background:rgba(1,14,36,.48)}.save-destination[hidden]{display:none}.save-destination label{white-space:nowrap}.save-destination select{height:34px;padding-block:5px}
      .actions{display:grid;grid-template-columns:minmax(0,1fr) auto auto;gap:8px}.button{min-height:40px;padding:9px 13px;border:1px solid rgba(214,227,255,.13);border-radius:12px;background:rgba(28,42,65,.72);color:var(--on-surface);font:inherit;font-weight:800;cursor:pointer;transition:border-color 150ms ease,background 150ms ease,box-shadow 150ms ease,transform 150ms ease}.button:hover:not(:disabled),.button:focus-visible:not(:disabled){border-color:rgba(83,224,118,.38);background:var(--surface-highest);outline:0;transform:translateY(-1px)}.button.primary{border-color:rgba(114,254,143,.5);background:linear-gradient(135deg,var(--primary-strong),#36ca61);color:var(--on-primary);box-shadow:0 10px 25px rgba(29,185,84,.16)}.button.primary:hover:not(:disabled),.button.primary:focus-visible:not(:disabled){border-color:var(--primary-strong);background:linear-gradient(135deg,#88ffa1,var(--primary));box-shadow:0 12px 30px rgba(83,224,118,.25)}.button:disabled{opacity:.4;cursor:default;transform:none}
      .status{display:flex;align-items:center;gap:7px;min-height:18px;color:rgba(214,227,255,.5);font-size:10px;font-weight:650}.status:not(:empty)::before{content:"";flex:0 0 6px;width:6px;height:6px;border-radius:50%;background:var(--primary);box-shadow:0 0 8px rgba(83,224,118,.5)}.status.error{color:#ffb4ab}.status.error::before{background:#ffb4ab;box-shadow:0 0 8px rgba(255,180,171,.42)}
      .minimized{height:auto!important;min-height:0}.minimized .body{display:none}
      @media(prefers-reduced-motion:reduce){*{transition:none!important}}
    </style>
    <section class="window" role="dialog" aria-label="Glosify Translator">
      <header class="header"><span class="mark" aria-hidden="true">G</span><span class="title"><span class="wordmark">Glosify</span><span class="product">Translator</span></span><span class="beta">Beta</span><button class="icon minimize" title="Minimize" aria-label="Minimize">−</button><button class="icon close" title="Close" aria-label="Close">×</button></header>
      <div class="body">
        <div class="languages">
          <div class="field"><label for="source-language">From</label><select id="source-language"></select></div>
          <button class="swap" title="Swap languages" aria-label="Swap languages">⇄</button>
          <div class="field"><label for="target-language">To</label><select id="target-language"></select></div>
        </div>
        <div class="field"><label for="source-text">Text</label><textarea class="source" id="source-text" placeholder="Type or paste text to translate" maxlength="8000"></textarea><span class="counter source-counter">0 / 8000</span></div>
        <div class="field"><label for="preferences">Optional preferences</label><textarea class="preferences" id="preferences" placeholder="For example: informal Mexican Spanish" maxlength="500"></textarea><span class="counter preference-counter">0 / 500</span></div>
        <div class="result-wrap"><label>Translation</label><div class="result placeholder" tabindex="0">Your translation will appear here.</div></div>
        <div class="save-destination" hidden><label for="save-language">Save to language</label><select id="save-language"></select></div>
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
    saveDestination: root.querySelector(".save-destination"),
    saveLanguage: root.querySelector("#save-language"),
    translate: root.querySelector(".translate"),
    copy: root.querySelector(".copy"),
    save: root.querySelector(".save"),
    status: root.querySelector(".status"),
  };
  const port = chrome.runtime.connect({ name: "translator-overlay" });
  const SETTINGS_SAVE_DELAY_MS = 400;
  let catalog = null;
  let initialized = false;
  let testHooksEnabled = false;
  let settingsSaveTimer = null;
  let restoredHeight = 625;
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

  function handleRuntimeMessage(message) {
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
  }
  chrome.runtime.onMessage.addListener(handleRuntimeMessage);

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
    if (minimized) {
      restoredHeight = Math.max(280, host.getBoundingClientRect().height);
      host.style.minHeight = "58px";
      host.style.height = "58px";
      host.style.resize = "none";
    } else {
      host.style.minHeight = "280px";
      host.style.height = `${restoredHeight}px`;
      host.style.resize = "both";
    }
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
    void chrome.runtime.sendMessage({
      type: "overlay:settings",
      settings: {
        sourceLanguage: state.sourceLanguage,
        targetLanguage: state.targetLanguage,
        preferences: state.preferences,
      },
    });
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
    state.busy = true;
    render();
    setStatus("Saving…");
    try {
      const response = await chrome.runtime.sendMessage({
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
    chrome.runtime.sendMessage({ type: "overlay:closed" }).catch(() => {});
    chrome.runtime.onMessage.removeListener(handleRuntimeMessage);
    port.disconnect();
    delete globalThis[INSTANCE_KEY];
    host.remove();
  }

  function installDragging() {
    let drag = null;
    elements.header.addEventListener("pointerdown", event => {
      if (event.target.closest("button")) return;
      const rect = host.getBoundingClientRect();
      drag = { x: event.screenX, y: event.screenY, left: rect.left, top: rect.top };
      host.style.left = `${rect.left}px`;
      host.style.right = "auto";
      elements.header.setPointerCapture(event.pointerId);
    });
    elements.header.addEventListener("pointermove", event => {
      if (!drag) return;
      const maxLeft = Math.max(0, innerWidth - host.offsetWidth);
      const maxTop = Math.max(0, innerHeight - 48);
      host.style.left = `${Math.min(maxLeft, Math.max(0, drag.left + event.screenX - drag.x))}px`;
      host.style.top = `${Math.min(maxTop, Math.max(0, drag.top + event.screenY - drag.y))}px`;
    });
    elements.header.addEventListener("pointerup", () => { drag = null; });
    elements.header.addEventListener("pointercancel", () => { drag = null; });
  }

  async function handleTestMessage(message) {
    switch (message.type) {
      case "test:overlay:state": {
        const rect = host.getBoundingClientRect();
        return {
          sourceLanguage: state.sourceLanguage,
          targetLanguage: state.targetLanguage,
          sourceText: state.sourceText,
          preferences: state.preferences,
          translatedText: state.result?.translatedText ?? null,
          requestId: state.requestId,
          sessionId: state.sessionId,
          saveLanguage: state.saveLanguage,
          saved: state.saved,
          swapDisabled: elements.swap.disabled,
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
      case "test:overlay:focus-input":
        elements.sourceText.focus();
        return true;
      case "test:overlay:translate":
        await translate();
        return state.result;
      case "test:overlay:save":
        await saveResult();
        return state.saved;
      case "test:overlay:set-save-language":
        elements.saveLanguage.value = message.languageCode;
        elements.saveLanguage.dispatchEvent(new Event("change"));
        return state.saveLanguage;
      case "test:overlay:swap":
        elements.swap.click();
        return true;
      case "test:overlay:minimize":
        elements.minimize.click();
        return elements.window.classList.contains("minimized");
      case "test:overlay:move-resize":
        host.style.left = `${message.left}px`;
        host.style.right = "auto";
        host.style.top = `${message.top}px`;
        host.style.width = `${message.width}px`;
        host.style.height = `${message.height}px`;
        return true;
      case "test:overlay:close":
        closeOverlay();
        return true;
      default:
        throw new Error("Unknown translator overlay test command.");
    }
  }
})();
