(() => {
  if (globalThis.__glosifyLiveSubtitlesInstalled) {
    return;
  }
  globalThis.__glosifyLiveSubtitlesInstalled = true;

  const ChatBuffer = globalThis.GlosifySubtitleChat?.ChatBuffer;
  const SubtitleAppearance = globalThis.GlosifySubtitleAppearance;
  if (!ChatBuffer || !SubtitleAppearance) {
    return;
  }
  let activeSessionId = null;
  const overlayInstanceId = createOverlayInstanceId();

  function createOverlayInstanceId() {
    if (typeof crypto.randomUUID === "function") {
      return crypto.randomUUID();
    }
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }

  // An extension reload invalidates the old isolated world but can leave its
  // closed-shadow host in the page until navigation. Replace that orphan so a
  // newly loaded build never renders two subtitle panels.
  document.getElementById("glosify-live-subtitles-host")?.remove();
  const host = document.createElement("div");
  host.id = "glosify-live-subtitles-host";
  const shadow = host.attachShadow({ mode: "closed" });
  shadow.innerHTML = `
    <style>
      :host { all: initial; }
      *, *::before, *::after { box-sizing: border-box; }
      .panel {
        position: fixed;
        z-index: 2147483647;
        right: max(18px, env(safe-area-inset-right));
        bottom: max(18px, env(safe-area-inset-bottom));
        width: min(460px, calc(100vw - 36px));
        height: min(440px, 58vh);
        min-width: min(300px, calc(100vw - 16px));
        min-height: 190px;
        max-width: calc(100vw - 16px);
        max-height: calc(100vh - 16px);
        display: flex;
        flex-direction: column;
        overflow: hidden;
        resize: both;
        border: 1px solid rgba(255, 255, 255, .16);
        border-radius: 16px;
        color: #f8fafc;
        background: rgba(10, 14, 23, .94);
        box-shadow: 0 18px 55px rgba(0, 0, 0, .48);
        backdrop-filter: blur(14px) saturate(125%);
        -webkit-backdrop-filter: blur(14px) saturate(125%);
        font-family: Inter, ui-sans-serif, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        font-size: 14px;
        line-height: 1.4;
      }
      .panel.hidden { display: none; }
      .panel:not(.minimized)::after {
        content: "";
        position: absolute;
        right: 5px;
        bottom: 5px;
        width: 12px;
        height: 12px;
        pointer-events: none;
        opacity: .58;
        background: linear-gradient(135deg,
          transparent 0 42%, rgba(255,255,255,.55) 43% 49%,
          transparent 50% 63%, rgba(255,255,255,.55) 64% 70%, transparent 71%);
      }
      .panel.minimized {
        height: 50px !important;
        min-height: 50px;
        resize: none;
      }
      .panel.minimized .body,
      .panel.minimized .footer { display: none; }
      .header {
        flex: 0 0 50px;
        min-width: 0;
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 8px 9px 8px 13px;
        border-bottom: 1px solid rgba(255, 255, 255, .09);
        cursor: move;
        cursor: grab;
        touch-action: none;
        user-select: none;
        -webkit-user-select: none;
      }
      .header:active { cursor: grabbing; }
      .panel.minimized .header { border-bottom-color: transparent; }
      .brand {
        flex: 0 0 28px;
        width: 28px;
        height: 28px;
        display: grid;
        place-items: center;
        border-radius: 9px;
        color: #fff;
        background: linear-gradient(145deg, #8b5cf6, #4f46e5);
        font-size: 15px;
        font-weight: 800;
        box-shadow: 0 5px 14px rgba(79, 70, 229, .3);
      }
      .heading { min-width: 0; flex: 1; }
      .title {
        overflow: hidden;
        color: #fff;
        font-size: 14px;
        font-weight: 740;
        letter-spacing: -.01em;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .subtitle {
        overflow: hidden;
        margin-top: 1px;
        color: rgba(226, 232, 240, .64);
        font-size: 11px;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .actions { display: flex; gap: 4px; }
      .action {
        min-width: 31px;
        height: 30px;
        padding: 0 8px;
        border: 0;
        border-radius: 8px;
        color: rgba(241, 245, 249, .8);
        background: transparent;
        font: inherit;
        font-size: 12px;
        font-weight: 650;
        cursor: pointer;
        touch-action: manipulation;
      }
      .action:hover { color: #fff; background: rgba(255, 255, 255, .1); }
      .action:focus-visible { outline: 2px solid #a78bfa; outline-offset: 1px; }
      .action:disabled { cursor: wait; opacity: .62; }
      .stop { color: #fca5a5; }
      .stop:hover:not(:disabled) { color: #fff; background: rgba(239, 68, 68, .24); }
      .minimize { font-size: 18px; line-height: 1; }
      .body {
        min-height: 0;
        flex: 1;
        display: flex;
        flex-direction: column;
      }
      .messages {
        min-height: 0;
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 10px;
        overflow-x: hidden;
        overflow-y: auto;
        padding: 14px;
        overscroll-behavior: contain;
        scrollbar-color: rgba(148, 163, 184, .48) transparent;
        scrollbar-width: thin;
      }
      .messages::-webkit-scrollbar { width: 8px; }
      .messages::-webkit-scrollbar-thumb {
        border: 2px solid transparent;
        border-radius: 8px;
        background: rgba(148, 163, 184, .42);
        background-clip: padding-box;
      }
      .empty {
        margin: auto;
        max-width: 270px;
        padding: 18px;
        color: rgba(203, 213, 225, .62);
        text-align: center;
        font-size: 13px;
      }
      .empty.hidden { display: none; }
      .history {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .message {
        align-self: flex-start;
        width: fit-content;
        max-width: 94%;
        padding: 10px 12px 7px;
        border: 1px solid rgba(167, 139, 250, .15);
        border-radius: 5px 14px 14px 14px;
        color: #f8fafc;
        background: rgba(79, 70, 229, .22);
        box-shadow: 0 5px 18px rgba(0, 0, 0, .13);
        overflow-wrap: anywhere;
      }
      .message[hidden] { display: none; }
      .message.current {
        border-style: dashed;
        border-color: rgba(167, 139, 250, .38);
        background: rgba(79, 70, 229, .13);
      }
      .translation {
        color: #fff;
        font-size: clamp(16px, 1.5vw, 20px);
        font-weight: 590;
        line-height: 1.38;
        letter-spacing: -.008em;
      }
      .meta {
        margin-top: 4px;
        color: rgba(203, 213, 225, .48);
        font-size: 10px;
        text-align: right;
      }
      .typing {
        display: inline-flex;
        gap: 3px;
        align-items: center;
        margin-top: 5px;
        color: rgba(196, 181, 253, .72);
        font-size: 10px;
      }
      .typing i {
        width: 3px;
        height: 3px;
        border-radius: 50%;
        background: currentColor;
        animation: pulse 1.1s infinite ease-in-out;
      }
      .typing i:nth-child(2) { animation-delay: 120ms; }
      .typing i:nth-child(3) { animation-delay: 240ms; }
      @keyframes pulse { 0%, 70%, 100% { opacity: .28; } 35% { opacity: 1; } }
      @media (prefers-reduced-motion: reduce) { .typing i { animation: none; } }
      .footer {
        flex: 0 0 34px;
        min-width: 0;
        display: flex;
        align-items: center;
        gap: 7px;
        padding: 0 14px;
        border-top: 1px solid rgba(255, 255, 255, .08);
        color: rgba(226, 232, 240, .66);
        font-size: 11px;
      }
      .status-dot {
        width: 7px;
        height: 7px;
        flex: 0 0 7px;
        border-radius: 50%;
        background: #34d399;
        box-shadow: 0 0 0 3px rgba(52, 211, 153, .12);
      }
      .status-text {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .panel.transparent:not(:hover):not(:focus-within) {
        resize: none;
        border-color: transparent;
        background: transparent;
        box-shadow: none;
        backdrop-filter: none;
        -webkit-backdrop-filter: none;
      }
      .panel.transparent:not(:hover):not(:focus-within)::after,
      .panel.transparent:not(:hover):not(:focus-within) .header,
      .panel.transparent:not(:hover):not(:focus-within) .empty,
      .panel.transparent:not(:hover):not(:focus-within) .meta,
      .panel.transparent:not(:hover):not(:focus-within) .typing,
      .panel.transparent:not(:hover):not(:focus-within) .footer {
        opacity: 0;
      }
      .panel.transparent:not(:hover):not(:focus-within) .message {
        border-color: transparent;
        background: transparent;
        box-shadow: none;
      }
      .panel.transparent:not(:hover):not(:focus-within) .translation {
        text-shadow: 0 1px 3px rgba(0, 0, 0, .95), 0 0 8px rgba(0, 0, 0, .72);
      }
      .panel.transparent:not(:hover):not(:focus-within) .messages {
        scrollbar-color: transparent transparent;
      }
      .panel.transparent:not(:hover):not(:focus-within) .messages::-webkit-scrollbar-thumb {
        background: transparent;
      }
    </style>
    <section class="panel hidden" role="region" aria-label="Glosify translated subtitle chat">
      <header class="header">
        <div class="brand" aria-hidden="true">G</div>
        <div class="heading">
          <div class="title">Live subtitle chat</div>
          <div class="subtitle">Drag the header · resize from the corner</div>
        </div>
        <div class="actions">
          <button class="action clear" type="button" title="Clear subtitle chat">Clear</button>
          <button class="action stop" type="button" title="Stop live subtitles">Stop</button>
          <button class="action minimize" type="button" title="Minimize subtitle chat" aria-label="Minimize subtitle chat">−</button>
        </div>
      </header>
      <div class="body">
        <div class="messages" role="log" aria-live="polite" aria-relevant="additions text">
          <div class="empty">Translated speech will appear here as a private, in-memory chat.</div>
          <div class="history"></div>
          <div class="message current" hidden>
            <div class="translation current-translation"></div>
            <div class="typing" aria-label="Translation in progress"><i></i><i></i><i></i></div>
          </div>
        </div>
      </div>
      <footer class="footer" role="status">
        <span class="status-dot" aria-hidden="true"></span>
        <span class="status-text"></span>
      </footer>
    </section>`;

  const panel = shadow.querySelector(".panel");
  const header = shadow.querySelector(".header");
  const messages = shadow.querySelector(".messages");
  const history = shadow.querySelector(".history");
  const empty = shadow.querySelector(".empty");
  const current = shadow.querySelector(".current");
  const currentTranslation = shadow.querySelector(".current-translation");
  const statusText = shadow.querySelector(".status-text");
  const clearButton = shadow.querySelector(".clear");
  const stopButton = shadow.querySelector(".stop");
  const minimizeButton = shadow.querySelector(".minimize");
  const chat = new ChatBuffer();
  let minimized = false;
  let drag = null;
  let constraintFrame = null;

  function attachToVisibleDocument() {
    const parent = document.fullscreenElement ?? document.documentElement;
    if (host.parentElement !== parent) {
      parent.append(host);
    }
    scheduleConstraint();
  }

  function show() {
    attachToVisibleDocument();
    panel.classList.remove("hidden");
  }

  function rebuildHistory() {
    const fragment = document.createDocumentFragment();
    for (const item of chat.messages) {
      const bubble = document.createElement("article");
      bubble.className = "message";

      const translationLine = document.createElement("div");
      translationLine.className = "translation";
      translationLine.textContent = item.text;
      bubble.append(translationLine);

      const meta = document.createElement("div");
      meta.className = "meta";
      meta.textContent = formatTime(item.timestamp);
      bubble.append(meta);
      fragment.append(bubble);
    }
    history.replaceChildren(fragment);
  }

  function renderChat({ committed = false } = {}) {
    const shouldFollow = isNearBottom();
    if (committed) {
      rebuildHistory();
    }
    currentTranslation.textContent = chat.translation;
    current.hidden = !chat.translation;
    empty.classList.toggle("hidden", chat.messages.length > 0 || !current.hidden);
    if (shouldFollow || committed) {
      requestAnimationFrame(() => {
        messages.scrollTop = messages.scrollHeight;
      });
    }
  }

  function clearTranscript() {
    chat.clear();
    history.replaceChildren();
    renderChat();
  }

  function clearAndHide() {
    clearTranscript();
    statusText.textContent = "";
    panel.classList.add("hidden");
  }

  function isNearBottom() {
    return messages.scrollHeight - messages.scrollTop - messages.clientHeight < 56;
  }

  function formatTime(timestamp) {
    return new Date(timestamp).toLocaleTimeString([], {
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  function toggleMinimized() {
    minimized = !minimized;
    panel.classList.toggle("minimized", minimized);
    minimizeButton.textContent = minimized ? "□" : "−";
    minimizeButton.title = minimized ? "Restore subtitle chat" : "Minimize subtitle chat";
    minimizeButton.setAttribute("aria-label", minimizeButton.title);
    if (!minimized) {
      requestAnimationFrame(() => {
        messages.scrollTop = messages.scrollHeight;
        constrainToViewport();
      });
    }
  }

  async function stopSubtitles() {
    if (stopButton.disabled) {
      return;
    }
    stopButton.disabled = true;
    stopButton.textContent = "Stopping…";
    statusText.textContent = "Stopping live subtitles…";
    try {
      const response = await chrome.runtime.sendMessage({ type: "overlay:stop" });
      if (!response?.ok) {
        throw new Error(response?.error || "Live subtitles could not be stopped.");
      }
    } catch (error) {
      statusText.textContent = error instanceof Error
        ? error.message
        : "Live subtitles could not be stopped.";
    } finally {
      stopButton.disabled = false;
      stopButton.textContent = "Stop";
    }
  }

  function beginDrag(event) {
    if (event.button !== 0 || event.target.closest("button")) {
      return;
    }
    const rect = panel.getBoundingClientRect();
    panel.style.left = `${rect.left}px`;
    panel.style.top = `${rect.top}px`;
    panel.style.right = "auto";
    panel.style.bottom = "auto";
    drag = {
      pointerId: event.pointerId,
      offsetX: event.clientX - rect.left,
      offsetY: event.clientY - rect.top,
    };
    header.setPointerCapture(event.pointerId);
    event.preventDefault();
  }

  function moveDrag(event) {
    if (!drag || event.pointerId !== drag.pointerId) {
      return;
    }
    const rect = panel.getBoundingClientRect();
    const maximumLeft = Math.max(8, window.innerWidth - rect.width - 8);
    const maximumTop = Math.max(8, window.innerHeight - rect.height - 8);
    panel.style.left = `${clamp(event.clientX - drag.offsetX, 8, maximumLeft)}px`;
    panel.style.top = `${clamp(event.clientY - drag.offsetY, 8, maximumTop)}px`;
  }

  function endDrag(event) {
    if (!drag || event.pointerId !== drag.pointerId) {
      return;
    }
    drag = null;
    if (header.hasPointerCapture(event.pointerId)) {
      header.releasePointerCapture(event.pointerId);
    }
  }

  function scheduleConstraint() {
    if (constraintFrame !== null) {
      cancelAnimationFrame(constraintFrame);
    }
    constraintFrame = requestAnimationFrame(() => {
      constraintFrame = null;
      constrainToViewport();
    });
  }

  function constrainToViewport() {
    if (panel.classList.contains("hidden")) {
      return;
    }
    const rect = panel.getBoundingClientRect();
    if (rect.width > window.innerWidth - 16) {
      panel.style.width = `${Math.max(180, window.innerWidth - 16)}px`;
    }
    if (!minimized && rect.height > window.innerHeight - 16) {
      panel.style.height = `${Math.max(140, window.innerHeight - 16)}px`;
    }
    const adjusted = panel.getBoundingClientRect();
    panel.style.left = `${clamp(adjusted.left, 8, Math.max(8, window.innerWidth - adjusted.width - 8))}px`;
    panel.style.top = `${clamp(adjusted.top, 8, Math.max(8, window.innerHeight - adjusted.height - 8))}px`;
    panel.style.right = "auto";
    panel.style.bottom = "auto";
  }

  function clamp(value, minimum, maximum) {
    return Math.min(Math.max(value, minimum), maximum);
  }

  clearButton.addEventListener("click", clearTranscript);
  stopButton.addEventListener("click", stopSubtitles);
  minimizeButton.addEventListener("click", toggleMinimized);
  header.addEventListener("pointerdown", beginDrag);
  header.addEventListener("pointermove", moveDrag);
  header.addEventListener("pointerup", endDrag);
  header.addEventListener("pointercancel", endDrag);
  new ResizeObserver(scheduleConstraint).observe(panel);

  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    switch (message?.type) {
      case "overlay:bind-session":
        activeSessionId = message.sessionId ?? null;
        sendResponse({ activeSessionId });
        break;
      case "overlay:get-state":
        sendResponse({
          activeSessionId,
          captionText: [...chat.messages.map(message => message.text), chat.translation]
            .filter(Boolean)
            .join("\n"),
          installed: true,
          overlayInstanceId,
          transparentSubtitles: panel.classList.contains("transparent"),
        });
        break;
      case "overlay:set-appearance":
        sendResponse({
          transparentSubtitles: SubtitleAppearance.applyTransparentSubtitles(
            panel,
            message.transparentSubtitles),
        });
        break;
      case "overlay:subtitle": {
        show();
        const result = chat.apply(message.event);
        if (result.changed) {
          renderChat(result);
        }
        break;
      }
      case "overlay:status":
        show();
        statusText.textContent = message.text ?? "";
        break;
      case "overlay:clear":
        activeSessionId = null;
        clearAndHide();
        break;
      default:
        break;
    }
  });

  document.addEventListener("fullscreenchange", attachToVisibleDocument);
  window.addEventListener("resize", scheduleConstraint);
  attachToVisibleDocument();
})();
