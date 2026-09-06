import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupHtmlPath = new URL("../popup/popup.html", import.meta.url);

test("popup renders sign-in errors and unknown balances without inventing an account", async () => {
  const html = await readFile(popupHtmlPath, "utf8");
  const signedInStart = html.indexOf('id="signed-in"');
  const signedInEnd = html.indexOf("</section>", signedInStart);
  const errorPosition = html.indexOf('id="error"');
  assert.ok(errorPosition > signedInEnd, "The shared error must not be inside the hidden signed-in section.");

  const elements = Object.fromEntries([
    "loading", "signed-out", "signed-in", "connect", "sign-out", "start", "saved",
    "email", "credits", "error", "server",
  ].map(id => [id, fakeElement(["signed-out", "signed-in", "error"].includes(id))]));
  const originalDocument = globalThis.document;
  const originalChrome = globalThis.chrome;
  const originalWindow = globalThis.window;
  globalThis.document = {
    querySelector(selector) {
      return elements[selector.slice(1)];
    },
  };
  let onState;
  globalThis.chrome = {
    runtime: {
      onMessage: { addListener(listener) { onState = listener; } },
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
    const source = (await readFile(new URL("../popup/popup.js", import.meta.url), "utf8"))
      .replace('"../config.js"', JSON.stringify(new URL("../config.store.js", import.meta.url).href));
    await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
    await new Promise(resolve => setImmediate(resolve));
    await elements.connect.listeners.get("click")();

    assert.equal(elements.error.textContent, "Glosify sign-in failed.");
    assert.equal(elements.error.classList.contains("hidden"), false);
    assert.equal(elements["signed-out"].classList.contains("hidden"), false);
    assert.equal(elements["signed-in"].classList.contains("hidden"), true);
    assert.equal(elements.server.textContent, "glosify.se");
    onState({ target: "popup", type: "state:update", state: { signedIn: true, availableCredits: null } });
    assert.equal(elements.email.textContent, "Account details unavailable");
    assert.equal(elements.credits.textContent, "Credits unavailable");
    assert.equal(elements.start.disabled, true);
    onState({ target: "popup", type: "state:update", state: { signedIn: true, email: "person@example.test", availableCredits: 0, catalog: {} } });
    assert.equal(elements.email.textContent, "person@example.test");
    assert.equal(elements.credits.textContent, "0 credits available");
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
