import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupHtmlPath = new URL("../popup/popup.html", import.meta.url);

test("signed-out sign-in errors remain visible", async () => {
  const html = await readFile(popupHtmlPath, "utf8");
  const signedInStart = html.indexOf('id="signed-in"');
  const signedInEnd = html.indexOf("</section>", signedInStart);
  const errorPosition = html.indexOf('id="error"');
  assert.ok(errorPosition > signedInEnd, "The shared error must not be inside the hidden signed-in section.");

  const elements = Object.fromEntries([
    "loading", "signed-out", "signed-in", "connect", "sign-out", "start", "saved",
    "email", "credits", "error",
  ].map(id => [id, fakeElement(["signed-out", "signed-in", "error"].includes(id))]));
  const originalDocument = globalThis.document;
  const originalChrome = globalThis.chrome;
  const originalWindow = globalThis.window;
  globalThis.document = {
    querySelector(selector) {
      return elements[selector.slice(1)];
    },
  };
  globalThis.chrome = {
    runtime: {
      onMessage: { addListener() {} },
      async sendMessage(message) {
        if (message.type === "popup:get-state") {
          return { ok: true, result: { signedIn: false, error: null } };
        }
        if (message.type === "popup:sign-in") {
          return { ok: false, error: "Glosify sign-in failed." };
        }
        return { ok: true, result: { signedIn: false } };
      },
    },
  };
  globalThis.window = { close() {} };

  try {
    await import(`../popup/popup.js?test=${Date.now()}`);
    await new Promise(resolve => setImmediate(resolve));
    await elements.connect.listeners.get("click")();

    assert.equal(elements.error.textContent, "Glosify sign-in failed.");
    assert.equal(elements.error.classList.contains("hidden"), false);
    assert.equal(elements["signed-out"].classList.contains("hidden"), false);
    assert.equal(elements["signed-in"].classList.contains("hidden"), true);
  } finally {
    restoreGlobal("document", originalDocument);
    restoreGlobal("chrome", originalChrome);
    restoreGlobal("window", originalWindow);
  }
});

function fakeElement(hidden) {
  const classes = new Set(hidden ? ["hidden"] : []);
  return {
    classList: {
      contains(name) { return classes.has(name); },
      toggle(name, force) {
        const enabled = force ?? !classes.has(name);
        if (enabled) classes.add(name);
        else classes.delete(name);
        return enabled;
      },
    },
    listeners: new Map(),
    addEventListener(type, listener) { this.listeners.set(type, listener); },
    disabled: false,
    textContent: "",
  };
}

function restoreGlobal(name, value) {
  if (value === undefined) Reflect.deleteProperty(globalThis, name);
  else globalThis[name] = value;
}
