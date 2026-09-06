(function installTranslatorHost() {
  const INSTANCE_KEY = "__glosifyTranslatorOverlay";
  if (globalThis[INSTANCE_KEY]) {
    globalThis[INSTANCE_KEY].focus();
    return;
  }

  const instanceId = globalThis.GlosifyTranslatorState.createRequestId();
  const frameUrl = chrome.runtime.getURL("overlay/translator.html");
  const extensionOrigin = new URL(frameUrl).origin;
  const host = document.createElement("iframe");
  host.id = "glosify-translator-host";
  host.title = "Glosify Translator";
  host.src = `${frameUrl}#${instanceId}`;
  host.style.cssText = "all:initial;position:fixed;z-index:2147483647;top:72px;right:28px;width:430px;height:625px;min-width:330px;min-height:280px;max-width:calc(100vw - 16px);max-height:calc(100vh - 16px);resize:both;overflow:auto;border:0;border-radius:20px;background:transparent;color-scheme:dark";
  if (innerWidth <= 500) {
    host.style.left = "8px";
    host.style.right = "8px";
    host.style.top = "8px";
    host.style.width = "calc(100vw - 16px)";
  }
  let frameId = null;
  let restoredHeight = 625;
  let minimized = false;
  let finishDragging = null;
  const instance = {
    focus() { host.contentWindow?.postMessage({ type: "host:focus" }, extensionOrigin); },
  };
  globalThis[INSTANCE_KEY] = instance;

  function handleRuntimeMessage(message, sender, sendResponse) {
    if (sender.id !== chrome.runtime.id) return false;
    if (message?.type === "host:claim") {
      const matches = message.instanceId === instanceId && host.isConnected
        && (frameId === null || frameId === message.frameId);
      if (matches) frameId = message.frameId;
      sendResponse({ matches });
    } else if (message?.type === "overlay:focus") {
      instance.focus();
      sendResponse({ focused: true });
    }
    return false;
  }
  chrome.runtime.onMessage.addListener(handleRuntimeMessage);

  // Only geometry crosses the page boundary. Sensitive data stays in the extension frame.
  function handleLayout(event) {
    if (event.source !== host.contentWindow || event.origin !== extensionOrigin) return;
    const message = event.data;
    if (message?.type === "frame:close") {
      finishDragging?.();
      chrome.runtime.onMessage.removeListener(handleRuntimeMessage);
      window.removeEventListener("message", handleLayout);
      delete globalThis[INSTANCE_KEY];
      host.remove();
    } else if (message?.type === "frame:minimize" && typeof message.minimized === "boolean") {
      if (minimized === message.minimized) return;
      minimized = message.minimized;
      if (minimized) restoredHeight = Math.max(280, host.getBoundingClientRect().height);
      host.style.minHeight = minimized ? "58px" : "280px";
      host.style.height = minimized ? "58px" : `${restoredHeight}px`;
      host.style.resize = minimized ? "none" : "both";
    } else if (message?.type === "frame:drag-start"
      && Number.isFinite(message.x) && Number.isFinite(message.y)) {
      finishDragging?.();
      const rect = host.getBoundingClientRect();
      host.style.left = `${rect.left}px`;
      host.style.right = "auto";
      // A page-side shield keeps pointer events stable while a cross-origin frame moves.
      const shield = document.createElement("div");
      shield.style.cssText = "all:initial;position:fixed;inset:0;z-index:2147483647;cursor:move;touch-action:none";
      shield.addEventListener("pointermove", pointer => {
        host.style.left = `${Math.min(Math.max(0, innerWidth - host.offsetWidth), Math.max(0, rect.left + pointer.screenX - message.x))}px`;
        host.style.top = `${Math.min(Math.max(0, innerHeight - 48), Math.max(0, rect.top + pointer.screenY - message.y))}px`;
      });
      const finish = () => {
        shield.remove();
        window.removeEventListener("blur", finish);
        if (finishDragging === finish) finishDragging = null;
      };
      shield.addEventListener("pointerup", finish);
      shield.addEventListener("pointercancel", finish);
      window.addEventListener("blur", finish, { once: true });
      document.documentElement.append(shield);
      finishDragging = finish;
    }
  }
  window.addEventListener("message", handleLayout);
  document.documentElement.append(host);
})();
