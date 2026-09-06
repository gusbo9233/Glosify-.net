import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";

const workerSource = (await readFile(new URL("../background/service-worker.js", import.meta.url), "utf8"))
  .replace(/^import .*;\n/gmu, "");
const stateSource = await readFile(new URL("../lib/translator-state.js", import.meta.url), "utf8");
const catalog = { languages: [{ code: "en" }, { code: "es" }], sourceLanguages: [{ code: "auto" }, { code: "en" }] };
const tokenKey = "glosifyTranslatorRefreshToken";
const popup = { id: "test", url: "chrome-extension://test/popup/popup.html" };

function deferred() {
  let resolve;
  const promise = new Promise(r => { resolve = r; });
  return { promise, resolve };
}

async function harness(override = () => undefined, callbackTransform = value => value) {
  const stored = { [tokenKey]: "valid-refresh" };
  const intervals = new Map();
  const timeouts = new Map();
  let nextTimer = 0;
  let keepAliveCalls = 0;
  const requests = [];
  const context = vm.createContext({
    URL, Headers, Response, AbortController, AbortSignal, DOMException, TextEncoder,
    crypto: globalThis.crypto, btoa,
    setInterval(fn, delay) { const id = ++nextTimer; intervals.set(id, { fn, delay }); return id; },
    clearInterval(id) { intervals.delete(id); },
    setTimeout(fn, delay) { const id = ++nextTimer; timeouts.set(id, { fn, delay }); return id; },
    clearTimeout(id) { timeouts.delete(id); },
    CONFIG: { glosifyBaseUrl: "https://api.test", testHooksEnabled: true },
    fetch: async (url, options) => {
      requests.push({ path: url.pathname, options });
      const replacement = override(url.pathname, options);
      if (replacement !== undefined) return replacement;
      if (["/api/auth/refresh", "/api/extension-auth/exchange"].includes(url.pathname)) {
        return Response.json({ accessToken: "access", refreshToken: "next-refresh", expiresIn: 3600 });
      }
      if (url.pathname === "/api/me") return Response.json({ email: "old@example.test", availableCredits: 42 });
      return Response.json(catalog);
    },
    chrome: {
      identity: {
        getRedirectURL: () => "https://test.chromiumapp.org/glosify",
        launchWebAuthFlow: async ({ url }) => {
          const authorize = new URL(url);
          const callback = new URL(authorize.searchParams.get("redirect_uri"));
          callback.searchParams.set("state", authorize.searchParams.get("state"));
          callback.searchParams.set("code", "single-use-code");
          return callbackTransform(callback.toString());
        },
      },
      runtime: {
        id: "test", getURL: path => `chrome-extension://test/${path}`,
        onConnect: { addListener() {} }, onMessage: { addListener() {} },
        sendMessage: async () => {},
        getPlatformInfo: async () => { keepAliveCalls++; },
      },
      storage: { local: {
        setAccessLevel: async () => {}, get: async () => ({ ...stored }),
        set: async value => { Object.assign(stored, value); },
        remove: async key => { delete stored[key]; },
      } },
      tabs: { sendMessage: async () => ({ matches: true }) },
    },
  });
  vm.runInContext(stateSource, context);
  vm.runInContext(workerSource, context);
  await vm.runInContext("initialization", context);
  return {
    stored, intervals, timeouts, requests,
    get keepAliveCalls() { return keepAliveCalls; },
    state: () => JSON.parse(vm.runInContext("JSON.stringify(publicState())", context)),
    message: (message, sender = popup) => context.handleMessage(message, sender),
    fetch: (options = {}) => context.apiFetch("/api/translator/translate", options),
  };
}

for (const status of [429, 500, 503]) {
  test(`refresh HTTP ${status} preserves credentials and recovers without sign-in`, async () => {
    let fail = true;
    const app = await harness(path => path === "/api/auth/refresh" && fail
      ? Response.json({ detail: "Temporarily unavailable" }, { status }) : undefined);
    await app.message({ type: "popup:get-state" });
    assert.equal(app.stored[tokenKey], "valid-refresh");
    assert.equal(app.state().signedIn, true);
    assert.equal(app.state().error, "Temporarily unavailable");
    fail = false;
    await app.message({ type: "popup:get-state" });
    assert.equal(app.state().email, "old@example.test");
    assert.equal(app.state().availableCredits, 42);
    assert.equal(app.state().error, null);
  });
}

test("a network failure preserves refresh credentials", async () => {
  const app = await harness(() => Promise.reject(new TypeError("Failed to fetch")));
  await app.message({ type: "popup:get-state" });
  assert.equal(app.stored[tokenKey], "valid-refresh");
  assert.equal(app.state().error, "Failed to fetch");
  assert.equal(app.intervals.size, 0);
  assert.equal(app.timeouts.size, 0);
});

for (const status of [401, 403]) {
  test(`refresh HTTP ${status} clears rejected authentication`, async () => {
    const app = await harness(() => Response.json({}, { status }));
    await app.message({ type: "popup:get-state" });
    assert.equal(app.stored[tokenKey], undefined);
    assert.equal(app.state().signedIn, false);
    assert.equal(app.state().email, null);
    assert.equal(app.state().catalog, null);
  });
}

test("a late account response cannot restore the signed-out account", async () => {
  const pendingMe = deferred();
  const started = deferred();
  const app = await harness(path => {
    if (path === "/api/me") { started.resolve(); return pendingMe.promise; }
  });
  const refresh = app.message({ type: "popup:get-state" });
  await started.promise;
  await app.message({ type: "popup:sign-out" });
  pendingMe.resolve(Response.json({ email: "old@example.test", availableCredits: 99 }));
  await refresh;
  assert.equal(app.state().signedIn, false);
  assert.equal(app.state().email, null);
  assert.equal(app.state().availableCredits, null);
  assert.equal(app.state().catalog, null);
  assert.equal(app.stored[tokenKey], undefined);
});

test("a late token response cannot restore credentials after sign-out", async () => {
  const pendingToken = deferred();
  const started = deferred();
  const app = await harness(path => {
    if (path === "/api/auth/refresh") { started.resolve(); return pendingToken.promise; }
  });
  const refresh = app.message({ type: "popup:get-state" });
  await started.promise;
  await app.message({ type: "popup:sign-out" });
  pendingToken.resolve(Response.json({ accessToken: "old-access", refreshToken: "old-refresh" }));
  await refresh;
  assert.equal(app.stored[tokenKey], undefined);
  assert.equal(app.state().signedIn, false);
  assert.equal(app.requests.filter(r => r.path === "/api/me").length, 0);
});

test("an old account response cannot overwrite a replacement session", async () => {
  const pendingMe = deferred();
  const started = deferred();
  let old = true;
  const app = await harness(path => {
    if (path !== "/api/me") return;
    if (old) { started.resolve(); return pendingMe.promise; }
    return Response.json({ email: "new@example.test", availableCredits: 10 });
  });
  const refresh = app.message({ type: "popup:get-state" });
  await started.promise;
  await app.message({ type: "popup:sign-out" });
  await app.message({ type: "test:seed-auth", refreshToken: "new-session" });
  old = false;
  await app.message({ type: "popup:get-state" });
  pendingMe.resolve(Response.json({ email: "old@example.test", availableCredits: 99 }));
  await refresh;
  assert.equal(app.state().email, "new@example.test");
  assert.equal(app.state().availableCredits, 10);
});

test("keepalive lasts through response-body reading and stops on completion", async () => {
  const body = deferred();
  const started = deferred();
  const app = await harness(path => {
    if (path !== "/api/translator/translate") return;
    started.resolve();
    return { status: 200, headers: {}, arrayBuffer: () => body.promise };
  });
  const pending = app.fetch();
  await started.promise;
  assert.equal(app.intervals.size, 1);
  const interval = [...app.intervals.values()][0];
  assert.equal(interval.delay, 20_000);
  interval.fn(); interval.fn();
  assert.equal(app.keepAliveCalls, 2);
  body.resolve(new TextEncoder().encode('{"translatedText":"Hola"}'));
  assert.equal((await pending).translatedText, "Hola");
  assert.equal(app.intervals.size, 0);
  assert.equal(app.timeouts.size, 0);
});

for (const reason of ["timeout", "cancel"]) {
  test(`request ${reason} stops keepalive and does not retry the paid request`, async () => {
    const started = deferred();
    const app = await harness((path, options) => {
      if (path !== "/api/translator/translate") return;
      started.resolve();
      return new Promise((resolve, reject) => {
        options.signal.addEventListener("abort", () => reject(options.signal.reason), { once: true });
      });
    });
    const controller = new AbortController();
    const pending = app.fetch({ signal: controller.signal });
    await started.promise;
    if (reason === "timeout") {
      const timeout = [...app.timeouts.values()][0];
      assert.equal(timeout.delay, 210_000);
      timeout.fn();
    } else controller.abort();
    await assert.rejects(pending, { name: reason === "timeout" ? "TimeoutError" : "AbortError" });
    assert.equal(app.intervals.size, 0);
    assert.equal(app.timeouts.size, 0);
    assert.equal(app.requests.filter(r => r.path === "/api/translator/translate").length, 1);
  });
}

test("web content cannot invoke privileged overlay or popup messages", async () => {
  const app = await harness();
  const sender = { id: "test", url: "https://untrusted.test", tab: { id: 1 }, frameId: 0 };
  await assert.rejects(app.message({ type: "overlay:translate", instanceId: "fake" }, sender));
  await assert.rejects(app.message({ type: "popup:sign-in" }, sender));
  assert.equal(app.requests.length, 0);
});

test("sign-in exchanges the valid callback and uses bearer-only, non-redirecting requests", async () => {
  const app = await harness();
  await app.message({ type: "popup:sign-in" });
  assert.equal(app.state().email, "old@example.test");
  assert.equal(app.state().availableCredits, 42);
  const exchanges = app.requests.filter(request => request.path === "/api/extension-auth/exchange");
  assert.equal(exchanges.length, 1);
  const exchange = JSON.parse(exchanges[0].options.body);
  assert.equal(exchange.code, "single-use-code");
  assert.equal(exchange.redirectUri, "https://test.chromiumapp.org/glosify");
  assert.match(exchange.codeVerifier, /^[A-Za-z0-9_-]{43}$/u);
  for (const { options } of app.requests) {
    assert.equal(options.redirect, "error");
    assert.equal(options.credentials, "omit");
    assert.equal(options.referrerPolicy, "no-referrer");
  }
});

for (const [name, change] of [
  ["wrong origin", value => value.replace("test.chromiumapp.org", "attacker.test")],
  ["wrong callback path", value => value.replace("/glosify?", "/wrong?")],
  ["wrong state", value => value.replace(/state=[^&]+/u, "state=wrong")],
  ["URL fragment", value => `${value}#unexpected`],
  ["cancelled sign-in", () => undefined],
]) {
  test(`sign-in rejects ${name} without exchanging tokens`, async () => {
    const app = await harness(undefined, change);
    await app.message({ type: "popup:sign-out" });
    await assert.rejects(app.message({ type: "popup:sign-in" }));
    assert.equal(app.requests.length, 0);
    assert.equal(app.stored[tokenKey], undefined);
    assert.equal(app.state().signedIn, false);
  });
}

test("an incompatible account response cannot invent an account or zero balance", async () => {
  const app = await harness(path => path === "/api/me" ? Response.json({}) : undefined);
  await app.message({ type: "popup:get-state" });
  assert.equal(app.state().email, null);
  assert.equal(app.state().availableCredits, null);
  assert.equal(app.state().catalog, null);
  assert.match(app.state().error, /incompatible account/u);
});
