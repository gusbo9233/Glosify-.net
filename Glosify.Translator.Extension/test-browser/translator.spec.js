import { test, expect, chromium } from "@playwright/test";
import { mkdtemp, rm } from "node:fs/promises";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const extensionPath = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)), "../artifacts/test");

test("translator overlay is isolated per tab and saves only after an explicit result", async () => {
  const mock = await startMock();
  const profile = await mkdtemp(path.join(os.tmpdir(), "glosify-translator-"));
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`],
  });
  try {
    const worker = context.serviceWorkers()[0]
      ?? await context.waitForEvent("serviceworker");
    const extensionId = new URL(worker.url()).host;
    const control = await context.newPage();
    await control.goto(`chrome-extension://${extensionId}/popup/popup.html`);
    const seeded = await control.evaluate(() => chrome.runtime.sendMessage({
      type: "test:seed-auth",
      refreshToken: "refresh-token",
    }));
    expect(seeded.ok).toBe(true);
    const account = await control.evaluate(() => chrome.runtime.sendMessage({ type: "popup:get-state" }));
    expect(account.result).toMatchObject({
      signedIn: true,
      email: "translator@example.test",
      availableCredits: 42,
    });

    const first = await context.newPage();
    await first.goto(`${mock.baseUrl}/page-one`);
    await first.bringToFront();
    await start(control);
    await start(control);
    await expect(first.locator("#glosify-translator-host")).toHaveCount(1);
    const firstTabId = await tabId(control, "/page-one");

    await overlay(control, firstTabId, "test:overlay:set-input", {
      sourceText: "Hello\nworld",
      preferences: "Informal Mexican Spanish",
      sourceLanguage: "auto",
      targetLanguage: "es",
    });
    await overlay(control, firstTabId, "test:overlay:translate");
    let state = await overlay(control, firstTabId, "test:overlay:state");
    expect(state).toMatchObject({
      sourceText: "Hello\nworld",
      preferences: "Informal Mexican Spanish",
      translatedText: "Hola\nmundo",
      saved: false,
    });
    expect(mock.translateRequests).toHaveLength(1);
    expect(mock.saveRequests).toHaveLength(0);

    await overlay(control, firstTabId, "test:overlay:move-resize", {
      left: 40, top: 50, width: 500, height: 520,
    });
    state = await overlay(control, firstTabId, "test:overlay:state");
    expect(state.rect).toMatchObject({ left: 40, top: 50, width: 500, height: 520 });
    expect(await overlay(control, firstTabId, "test:overlay:minimize")).toBe(true);
    expect((await overlay(control, firstTabId, "test:overlay:state")).minimized).toBe(true);
    await overlay(control, firstTabId, "test:overlay:minimize");
    expect(await overlay(control, firstTabId, "test:overlay:save")).toBe(true);
    expect(mock.saveRequests).toHaveLength(1);
    expect(mock.saveRequests[0].preferences).toBe("Informal Mexican Spanish");

    const second = await context.newPage();
    await second.goto(`${mock.baseUrl}/page-two`);
    await second.bringToFront();
    await start(control);
    await expect(second.locator("#glosify-translator-host")).toHaveCount(1);
    await expect(first.locator("#glosify-translator-host")).toHaveCount(1);

    await first.goto(`${mock.baseUrl}/navigated`);
    await expect(first.locator("#glosify-translator-host")).toHaveCount(0);
    await overlay(control, await tabId(control, "/page-two"), "test:overlay:close");
    await expect(second.locator("#glosify-translator-host")).toHaveCount(0);
  } finally {
    await context.close();
    await rm(profile, { recursive: true, force: true });
    await mock.close();
  }
});

test("insufficient credits use Problem Details and a paid translation is not retried", async () => {
  const mock = await startMock({ translateFailure: { status: 402, detail: "Add credits before translating." } });
  const profile = await mkdtemp(path.join(os.tmpdir(), "glosify-translator-error-"));
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`],
  });
  try {
    const worker = context.serviceWorkers()[0] ?? await context.waitForEvent("serviceworker");
    const control = await context.newPage();
    await control.goto(`chrome-extension://${new URL(worker.url()).host}/popup/popup.html`);
    await control.evaluate(() => chrome.runtime.sendMessage({ type: "test:seed-auth", refreshToken: "refresh-token" }));
    await control.evaluate(() => chrome.runtime.sendMessage({ type: "popup:get-state" }));
    const page = await context.newPage();
    await page.goto(`${mock.baseUrl}/error-page`);
    await page.bringToFront();
    await start(control);
    const id = await tabId(control, "/error-page");
    await overlay(control, id, "test:overlay:set-input", { sourceText: "Hello", targetLanguage: "es" });
    await overlay(control, id, "test:overlay:translate");
    const state = await overlay(control, id, "test:overlay:state");
    expect(state).toMatchObject({
      translatedText: null,
      statusText: "Add credits before translating.",
      statusError: true,
    });
    expect(mock.translateRequests).toHaveLength(1);
  } finally {
    await context.close();
    await rm(profile, { recursive: true, force: true });
    await mock.close();
  }
});

test("an expired refresh token returns the popup to signed-out state", async () => {
  const mock = await startMock({ refreshFailure: true });
  const profile = await mkdtemp(path.join(os.tmpdir(), "glosify-translator-auth-"));
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`],
  });
  try {
    const worker = context.serviceWorkers()[0] ?? await context.waitForEvent("serviceworker");
    const control = await context.newPage();
    await control.goto(`chrome-extension://${new URL(worker.url()).host}/popup/popup.html`);
    await control.evaluate(() => chrome.runtime.sendMessage({ type: "test:seed-auth", refreshToken: "expired" }));
    const response = await control.evaluate(() => chrome.runtime.sendMessage({ type: "popup:get-state" }));
    expect(response.result).toMatchObject({ signedIn: false, status: "disconnected" });
    expect(mock.refreshRequests).toBe(1);
  } finally {
    await context.close();
    await rm(profile, { recursive: true, force: true });
    await mock.close();
  }
});

async function start(control) {
  const response = await control.evaluate(() => chrome.runtime.sendMessage({ type: "popup:start" }));
  expect(response.ok, JSON.stringify(response)).toBe(true);
}

async function tabId(control, pathFragment) {
  return control.evaluate(async fragment => {
    const tabs = await chrome.tabs.query({});
    return tabs.find(tab => tab.url?.includes(fragment))?.id;
  }, pathFragment);
}

async function overlay(control, id, type, extra = {}) {
  return control.evaluate(async ({ tab, message }) => chrome.tabs.sendMessage(tab, message), {
    tab: id,
    message: { type, ...extra },
  });
}

async function startMock(options = {}) {
  const translateRequests = [];
  const saveRequests = [];
  let refreshRequests = 0;
  const server = http.createServer(async (request, response) => {
    const body = await readJson(request);
    response.setHeader("Content-Type", "application/json");
    if (request.url === "/api/auth/refresh") {
      refreshRequests++;
      if (options.refreshFailure) {
        response.statusCode = 401;
        response.end(JSON.stringify({ title: "Unauthorized", detail: "Session expired." }));
        return;
      }
      response.end(JSON.stringify({ accessToken: "access", refreshToken: "refresh-next", expiresIn: 3600 }));
      return;
    }
    if (request.url === "/api/me") {
      response.end(JSON.stringify({ email: "translator@example.test", availableCredits: 42 }));
      return;
    }
    if (request.url === "/api/translator/catalog") {
      const languages = [{ code: "en", name: "English", nativeName: "English" }, { code: "es", name: "Spanish", nativeName: "Español" }];
      response.end(JSON.stringify({
        languages,
        sourceLanguages: [{ code: "auto", name: "Auto-detect", nativeName: "Auto-detect" }, ...languages],
        maxSourceCharacters: 8000,
        maxPreferenceCharacters: 500,
        maxTranslatedCharacters: 16000,
      }));
      return;
    }
    if (request.url === "/api/translator/translate") {
      translateRequests.push(body);
      if (options.translateFailure) {
        response.statusCode = options.translateFailure.status;
        response.end(JSON.stringify({
          title: "Translation unavailable",
          detail: options.translateFailure.detail,
          error: options.translateFailure.detail,
        }));
        return;
      }
      response.end(JSON.stringify({
        sourceText: body.sourceText,
        sourceLanguage: body.sourceLanguage,
        detectedSourceLanguage: "en",
        targetLanguage: body.targetLanguage,
        translatedText: "Hola\nmundo",
        remainingCredits: 39,
      }));
      return;
    }
    if (request.url === "/api/translator/saved-translations") {
      saveRequests.push(body);
      response.statusCode = 201;
      response.end(JSON.stringify({ id: "11111111-1111-4111-8111-111111111111", createdAt: new Date().toISOString(), historyUrl: "/Translations/11111111-1111-4111-8111-111111111111" }));
      return;
    }
    response.setHeader("Content-Type", "text/html");
    response.end("<!doctype html><title>Translator test page</title><main>Page content</main>");
  });
  await new Promise(resolve => server.listen(4178, "127.0.0.1", resolve));
  return {
    baseUrl: "http://127.0.0.1:4178",
    translateRequests,
    saveRequests,
    get refreshRequests() { return refreshRequests; },
    close: () => new Promise(resolve => server.close(resolve)),
  };
}

async function readJson(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const text = Buffer.concat(chunks).toString("utf8");
  return text ? JSON.parse(text) : null;
}
