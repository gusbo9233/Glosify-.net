import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const worker = readFileSync(new URL("../background/service-worker.js", import.meta.url), "utf8");
const setup = worker.slice(worker.indexOf("const tabAuth ="), worker.indexOf("chrome.runtime.onMessage.addListener"));
const refresh = worker.slice(worker.indexOf("async function refreshAccountState()"), worker.indexOf("async function setTargetLanguage"));

for (const failingPath of [null, "/api/me", "/api/realtime-translation/catalog"]) {
  test(`login feedback after account loading: ${failingPath ?? "success"}`, async () => {
    let fail = Boolean(failingPath);
    const state = { notice: "Finish signing in", targetLanguage: "en", sourceLanguage: "auto", translationMode: "enhanced", paidServicesAvailable: true };
    const auth = vm.runInNewContext(`
      const initialization = Promise.resolve();
      const CONFIG = { glosifyBaseUrl: "https://example.invalid" };
      const refreshToken = "token";
      ${refresh}
      ${setup}
      tabAuth;
    `, {
      state, URL, chrome: {},
      createTabAuth: options => options,
      fetch: async () => Response.json({}),
      acceptTokenResponse: async () => {},
      broadcastState: () => {},
      normalizeError: error => error,
      refreshPaidServiceStatus: async () => {},
      selectCurrentTranslationModes: modes => modes,
      apiFetch: async path => {
        if (fail && path === failingPath) throw new Error("Account settings temporarily unavailable");
        if (path === "/api/me") return { email: "user@example.test", availableCredits: 10 };
        return { availableCredits: 10, modes: [{ code: "enhanced", name: "Best" }], languages: [{ code: "en" }], sourceLanguages: [{ code: "auto" }] };
      },
    });
    await auth.complete({});
    if (failingPath) {
      assert.equal(state.status, "error");
      assert.equal(state.error, "Account settings temporarily unavailable");
      assert.equal(state.notice, null);
      fail = false;
      await auth.complete({});
    }
    assert.equal(state.status, "ready");
    assert.equal(state.error, null);
    assert.equal(state.notice, "Connected to GlobeGlotter. Choose a tab with audio to start subtitles.");
  });
}
