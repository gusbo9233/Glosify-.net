import test from "node:test";
import assert from "node:assert/strict";
import { createTabAuth } from "../lib/tab-auth.js";

const redirectUri = "https://extension.chromiumapp.org/glosify";
const options = { authorizeUrl: "https://glosify.se/extension/connect", redirectUri,
  oauthState: "expected-state", codeVerifier: "private-verifier" };
const callback = `${redirectUri}?state=expected-state&code=one-time-code`;
function harness() {
  const storage = {};
  const tabs = new Map();
  const completed = [], errors = [];
  let clock = 0, nextId = 1;
  const event = () => {
    const listeners = [];
    return { reset: () => { listeners.length = 0; }, addListener: fn => listeners.push(fn),
      emit: (...args) => listeners.forEach(fn => fn(...args)) };
  };
  const chrome = {
    identity: { getRedirectURL: () => redirectUri },
    storage: { session: {
      get: async key => ({ [key]: storage[key] }),
      set: async values => Object.assign(storage, values),
      remove: async key => { delete storage[key]; },
    } },
    webNavigation: { onBeforeNavigate: event(), onCommitted: event(), onErrorOccurred: event() },
    tabs: {
      onRemoved: event(),
      create: async data => { const tab = { id: nextId++, ...data }; tabs.set(tab.id, tab); return tab; },
      update: async (id, data) => {
        if (!tabs.has(id)) throw new Error("Missing tab");
        Object.assign(tabs.get(id), data);
      },
      remove: async id => { tabs.delete(id); chrome.tabs.onRemoved.emit(id); },
    },
  };
  const attach = () => createTabAuth({ chrome, now: () => clock,
    complete: async credentials => completed.push(credentials),
    failed: async error => errors.push(error.message) });
  return { auth: attach(), restart: () => {
    Object.values(chrome.webNavigation).forEach(event => event.reset());
    chrome.tabs.onRemoved.reset();
    return attach();
  }, chrome, storage, tabs, completed, errors,
    expire: () => { clock = 600001; } };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

test("opens a normal tab and redeems the matching callback once", async () => {
  const h = harness();
  await h.auth.start(options);
  assert.equal(h.tabs.get(1).url, options.authorizeUrl);
  await h.auth.start(options);
  assert.equal(h.tabs.size, 1);
  h.chrome.webNavigation.onBeforeNavigate.emit({ tabId: 1, frameId: 0, url: callback });
  h.chrome.webNavigation.onErrorOccurred.emit({ tabId: 1, frameId: 0, url: callback });
  await settle();
  assert.deepEqual(h.completed, [{ code: "one-time-code", redirectUri, codeVerifier: "private-verifier" }]);
  assert.equal(h.tabs.size, 0);
  assert.deepEqual(h.storage, {});
  assert.deepEqual(h.errors, []);
});

test("ignores callbacks from other tabs, subframes, origins and paths", async () => {
  const h = harness();
  await h.auth.start(options);
  for (const details of [
    { tabId: 2, frameId: 0, url: callback },
    { tabId: 1, frameId: 1, url: callback },
    { tabId: 1, frameId: 0, url: callback.replace("extension.chromiumapp.org", "evil.example") },
    { tabId: 1, frameId: 0, url: callback.replace("/glosify?", "/glosify/other?") },
  ]) h.chrome.webNavigation.onBeforeNavigate.emit(details);
  await settle();
  assert.equal(h.completed.length, 0);
  assert.equal(h.tabs.size, 1);
  assert.ok(h.storage.glosifyPendingTabAuth);
});

for (const scenario of ["state", "expired", "no-code"]) {
  test(`rejects ${scenario} callbacks without exchanging credentials`, async () => {
    const h = harness();
    await h.auth.start(options);
    if (scenario === "expired") h.expire();
    const url = scenario === "state" ? callback.replace("expected-state", "wrong-state")
      : scenario === "no-code" ? callback.replace("&code=one-time-code", "") : callback;
    h.chrome.webNavigation.onBeforeNavigate.emit({ tabId: 1, frameId: 0, url });
    await settle();
    assert.equal(h.completed.length, 0);
    assert.equal(h.errors.length, 1);
    assert.equal(h.tabs.size, 0);
    assert.deepEqual(h.storage, {});
  });
}

test("closing the login tab cancels and permits retry", async () => {
  const h = harness();
  await h.auth.start(options);
  await h.chrome.tabs.remove(1);
  await settle();
  assert.match(h.errors[0], /cancelled/);
  assert.deepEqual(h.storage, {});
  await h.auth.start(options);
  assert.equal(h.tabs.size, 1);
});

test("sign-out clears a pending login before a later callback", async () => {
  const h = harness();
  await h.auth.start(options);
  await h.auth.cancel();
  h.chrome.webNavigation.onBeforeNavigate.emit({ tabId: 1, frameId: 0, url: callback });
  await settle();
  assert.equal(h.completed.length, 0);
  assert.deepEqual(h.storage, {});
});


test("a restarted worker finishes using only pending session storage", async () => {
  const h = harness();
  await h.auth.start(options);
  h.restart();
  h.chrome.webNavigation.onErrorOccurred.emit({ tabId: 1, frameId: 0, url: callback });
  await settle();
  assert.deepEqual(h.completed, [{ code: "one-time-code", redirectUri, codeVerifier: "private-verifier" }]);
  assert.deepEqual(h.storage, {});
  assert.equal(h.tabs.size, 0);
});

test("exchange failure is reported and clears the login tab", async () => {
  const h = harness();
  h.completed.push = () => { throw new Error("Service unavailable. Try again."); };
  await h.auth.start(options);
  h.chrome.webNavigation.onBeforeNavigate.emit({ tabId: 1, frameId: 0, url: callback });
  await settle();
  assert.deepEqual(h.errors, ["Service unavailable. Try again."]);
  assert.equal(h.tabs.size, 0);
  assert.deepEqual(h.storage, {});
});
