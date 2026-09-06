import { test, expect, chromium } from "@playwright/test";
import { mkdtemp, rm } from "node:fs/promises";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const extensionPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../artifacts/test");

async function setup(options = {}) {
  const mock = await startMock(options);
  const profile = await mkdtemp(path.join(os.tmpdir(), "glosify-translator-"));
  let context;
  try {
    context = await chromium.launchPersistentContext(profile, {
      headless: false,
      args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`],
    });
    const worker = context.serviceWorkers()[0] ?? await context.waitForEvent("serviceworker");
    const extensionId = new URL(worker.url()).host;
    const control = await context.newPage();
    await control.goto(`chrome-extension://${extensionId}/popup/popup.html`);
    await control.evaluate(() => chrome.runtime.sendMessage({ type: "test:seed-auth", refreshToken: "refresh-token" }));
    const account = await control.evaluate(() => chrome.runtime.sendMessage({ type: "popup:get-state" }));
    return {
      mock, context, control, account, extensionId,
      async open(suffix) {
        const page = await context.newPage();
        await page.goto(`${mock.baseUrl}/${suffix}`);
        await page.bringToFront();
        await start(control);
        const frame = page.frameLocator("#glosify-translator-host");
        await expect(frame.locator("#target-language")).toHaveValue("en");
        return { page, frame };
      },
      async close() {
        await context.close();
        await rm(profile, { recursive: true, force: true });
        await mock.close();
      },
    };
  } catch (error) {
    await context?.close();
    await rm(profile, { recursive: true, force: true });
    await mock.close();
    throw error;
  }
}

async function start(control) {
  const response = await control.evaluate(() => chrome.runtime.sendMessage({ type: "popup:start" }));
  expect(response.ok, JSON.stringify(response)).toBe(true);
}

async function translate(frame, source = "Hello\nworld") {
  await frame.locator("#source-text").fill(source);
  await frame.locator("#target-language").selectOption("es");
  await frame.getByRole("button", { name: "Translate", exact: true }).click();
}

test("translator stays private to its extension frame and requires explicit translation and save", async () => {
  const app = await setup();
  try {
    expect(app.account.result).toMatchObject({ signedIn: true, email: "translator@example.test", availableCredits: 42 });
    const { page, frame } = await app.open("privacy");
    await page.evaluate(() => {
      window.hostKeys = [];
      window.hostMessages = [];
      document.addEventListener("keydown", event => {
        window.hostKeys.push(event.key);
        event.preventDefault();
      }, true);
      window.addEventListener("message", event => window.hostMessages.push(event.data));
    });
    const boundary = await page.locator("#glosify-translator-host").evaluate(element => {
      let denied = false;
      try { void element.contentWindow.document; } catch (error) { denied = error.name === "SecurityError"; }
      return { documentIsNull: element.contentDocument === null, denied };
    });
    expect(boundary).toEqual({ documentIsNull: true, denied: true });
    await frame.locator("#source-text").pressSequentially("private typed text");
    await expect(frame.locator("#source-text")).toHaveValue("private typed text");
    expect(await page.evaluate(() => window.hostKeys)).toEqual([]);
    await page.locator("#glosify-translator-host").evaluate(element => {
      element.contentWindow.postMessage({ type: "overlay:translate", request: { sourceText: "page injection" } }, "*");
      element.contentWindow.postMessage({ type: "test:overlay:set-input", sourceText: "page injection" }, "*");
    });
    await expect(frame.locator("#source-text")).toHaveValue("private typed text");
    await frame.locator("#preferences").fill("Informal Mexican Spanish");
    await translate(frame);
    await expect(frame.locator(".result")).toHaveText("Hola\nmundo");
    expect(app.mock.translateRequests).toHaveLength(1);
    expect(app.mock.saveRequests).toHaveLength(0);
    await frame.locator("#save-language").selectOption("en");
    await frame.getByRole("button", { name: "Save", exact: true }).click();
    await expect(frame.getByRole("button", { name: "Saved", exact: true })).toBeDisabled();
    expect(app.mock.saveRequests).toHaveLength(1);
    expect(app.mock.saveRequests[0]).toMatchObject({
      languageCode: "en", preferences: "Informal Mexican Spanish",
      sourceText: "Hello\nworld", translatedText: "Hola\nmundo",
      translationOperationId: "22222222-2222-4222-8222-222222222222",
    });
    expect(app.mock.saveRequests[0].sessionId).toMatch(/^[0-9a-f-]{36}$/u);
    expect(app.mock.saveRequests[0].requestId).toMatch(/^[0-9a-f-]{36}$/u);
    const exposed = JSON.stringify(await page.evaluate(() => window.hostMessages));
    for (const secret of ["private typed text", "Hello", "Hola", "Informal Mexican Spanish", "refresh-token"]) {
      expect(exposed).not.toContain(secret);
    }
  } finally { await app.close(); }
});

test("Gmail-style shortcuts and Space stay inside both translator inputs", async () => {
  const app = await setup({ pageCsp: "default-src 'none'; frame-src 'none'" });
  try {
    const { page, frame } = await app.open("mail-shortcuts");
    await page.evaluate(() => {
      document.body.style.height = "4000px";
      window.scrollTo(0, 120);
      window.mailActions = [];
      for (const type of ["keydown", "keypress", "keyup"]) {
        document.addEventListener(type, event => {
          if (["o", "e", "j", "k", " "].includes(event.key)) {
            window.mailActions.push({ type, key: event.key });
            event.preventDefault();
            window.scrollBy(0, 200);
          }
        }, true);
      }
    });
    const source = frame.locator("#source-text");
    const preferences = frame.locator("#preferences");
    await source.click();
    const sourceScroll = await page.evaluate(() => window.scrollY);
    await page.keyboard.type("hello o e j k world");
    await page.keyboard.press("Space");
    await page.keyboard.press("Space");
    await expect(source).toHaveValue("hello o e j k world  ");
    expect(await page.evaluate(() => window.scrollY)).toBe(sourceScroll);
    await preferences.click();
    const preferenceScroll = await page.evaluate(() => window.scrollY);
    await page.keyboard.type("informal o e j k");
    await page.keyboard.press("Space");
    await expect(preferences).toHaveValue("informal o e j k ");
    expect(await page.evaluate(() => window.scrollY)).toBe(preferenceScroll);
    await frame.getByRole("button", { name: "Minimize", exact: true }).click();
    await frame.getByRole("button", { name: "Restore", exact: true }).click();
    await source.click();
    const restoredScroll = await page.evaluate(() => window.scrollY);
    await source.evaluate(element => element.setSelectionRange(element.value.length, element.value.length));
    await page.keyboard.type("o e j k");
    await page.keyboard.press("Space");
    await expect(source).toHaveValue("hello o e j k world  o e j k ");
    expect(await page.evaluate(() => window.mailActions)).toEqual([]);
    expect(await page.evaluate(() => window.scrollY)).toBe(restoredScroll);
    expect(app.mock.translateRequests).toHaveLength(0);
  } finally { await app.close(); }
});

test("reopening an existing box preserves its result, preferences and languages across tabs", async () => {
  const app = await setup();
  try {
    const first = await app.open("first");
    await first.frame.locator("#preferences").fill("First preference");
    await translate(first.frame);
    await expect(first.frame.locator(".result")).toHaveText("Hola\nmundo");
    // A new box starts with global defaults, then changes them independently.
    const page = await app.context.newPage();
    await page.goto(`${app.mock.baseUrl}/second`);
    await page.bringToFront();
    await start(app.control);
    const second = page.frameLocator("#glosify-translator-host");
    await expect(second.locator("#target-language")).toHaveValue("es");
    await second.locator("#source-language").selectOption("es");
    await second.locator("#target-language").selectOption("en");
    await second.locator("#preferences").fill("Second preference");
    await expect.poll(() => app.control.evaluate(async () =>
      (await chrome.storage.local.get("glosifyTranslatorPreferences")).glosifyTranslatorPreferences
    )).toBe("Second preference");
    await first.page.bringToFront();
    await start(app.control);
    await expect(first.page.locator("#glosify-translator-host")).toHaveCount(1);
    await expect(first.frame.locator("#source-language")).toHaveValue("auto");
    await expect(first.frame.locator("#target-language")).toHaveValue("es");
    await expect(first.frame.locator("#preferences")).toHaveValue("First preference");
    await expect(first.frame.locator(".result")).toHaveText("Hola\nmundo");
    await first.frame.getByRole("button", { name: "Save", exact: true }).click();
    await expect(first.frame.getByRole("button", { name: "Saved", exact: true })).toBeVisible();
    expect(app.mock.saveRequests[0].preferences).toBe("First preference");
    expect(app.mock.saveRequests[0].targetLanguage).toBe("es");
    await first.page.reload();
    await expect(first.page.locator("#glosify-translator-host")).toHaveCount(0);
    await expect(page.locator("#glosify-translator-host")).toHaveCount(1);
  } finally { await app.close(); }
});

test("move, resize, minimize, swap and close preserve the overlay lifecycle", async () => {
  const app = await setup();
  try {
    const { page, frame } = await app.open("lifecycle");
    const host = page.locator("#glosify-translator-host");
    expect(await host.evaluate(element => getComputedStyle(element).resize)).toBe("both");
    const before = await host.boundingBox();
    await page.mouse.move(before.x + 120, before.y + 25);
    await page.mouse.down();
    await page.mouse.move(20, before.y + 85, { steps: 12 });
    await page.mouse.up();
    await expect.poll(async () => (await host.boundingBox()).x).toBeCloseTo(0, 0);
    await expect.poll(async () => (await host.boundingBox()).y).toBeCloseTo(before.y + 60, 0);
    await host.evaluate(element => { element.style.width = "500px"; element.style.height = "520px"; });
    await expect.poll(async () => (await host.boundingBox()).height).toBe(520);
    await frame.getByRole("button", { name: "Minimize", exact: true }).click();
    await expect.poll(async () => (await host.boundingBox()).height).toBe(58);
    await start(app.control);
    await expect.poll(async () => (await host.boundingBox()).height).toBe(520);
    await translate(frame);
    await expect(frame.locator(".result")).toHaveText("Hola\nmundo");
    await frame.getByRole("button", { name: "Save", exact: true }).click();
    await expect(frame.getByRole("button", { name: "Saved", exact: true })).toBeVisible();
    const firstSession = app.mock.saveRequests[0].sessionId;
    await frame.getByRole("button", { name: "Swap languages", exact: true }).click();
    await expect(frame.locator("#source-language")).toHaveValue("es");
    await expect(frame.locator("#target-language")).toHaveValue("en");
    await expect(frame.locator("#source-text")).toHaveValue("Hola\nmundo");
    await expect(frame.locator(".save")).toBeDisabled();
    await frame.getByRole("button", { name: "Close", exact: true }).click();
    await expect(host).toHaveCount(0);
    await start(app.control);
    await expect(frame.locator("#source-text")).toHaveValue("");
    await expect(frame.locator(".result")).toHaveClass(/placeholder/u);
    await frame.locator("#source-language").selectOption("auto");
    await frame.locator("#source-text").fill("Hello");
    await frame.getByRole("button", { name: "Translate", exact: true }).click();
    await expect(frame.locator(".result")).toHaveText("Hola\nmundo");
    await expect(frame.getByRole("button", { name: "Swap languages", exact: true })).toBeDisabled();
    await frame.getByRole("button", { name: "Save", exact: true }).click();
    await expect(frame.getByRole("button", { name: "Saved", exact: true })).toBeVisible();
    expect(app.mock.saveRequests[1].sessionId).not.toBe(firstSession);
  } finally { await app.close(); }
});

test("insufficient credits display Problem Details without retrying the paid request", async () => {
  const app = await setup({ translateFailure: { status: 402, detail: "Add credits before translating." } });
  try {
    const { frame } = await app.open("credits");
    await translate(frame);
    await expect(frame.locator(".status")).toHaveText("Add credits before translating.");
    await expect(frame.locator(".status")).toHaveClass(/error/u);
    await expect(frame.locator(".result")).toHaveClass(/placeholder/u);
    expect(app.mock.translateRequests).toHaveLength(1);
  } finally { await app.close(); }
});

test("an expired refresh token returns the popup to signed-out state", async () => {
  const app = await setup({ refreshFailure: true });
  try {
    expect(app.account.result).toMatchObject({ signedIn: false, status: "disconnected" });
    expect(app.mock.refreshRequests).toBe(1);
  } finally { await app.close(); }
});

test("a translation taking longer than 30 seconds returns once and remains saveable", async () => {
  test.setTimeout(70_000);
  const app = await setup({ translateDelay: 35_000 });
  try {
    const { frame } = await app.open("slow");
    await translate(frame);
    await expect(frame.locator(".status")).toHaveText("Translating…");
    await expect(frame.locator(".result")).toHaveText("Hola\nmundo", { timeout: 50_000 });
    await frame.getByRole("button", { name: "Save", exact: true }).click();
    await expect(frame.getByRole("button", { name: "Saved", exact: true })).toBeVisible();
    expect(app.mock.translateRequests).toHaveLength(1);
    expect(app.mock.saveRequests).toHaveLength(1);
  } finally { await app.close(); }
});

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
      if (options.translateDelay) await new Promise(resolve => setTimeout(resolve, options.translateDelay));
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
        translationOperationId: "22222222-2222-4222-8222-222222222222",
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
      response.end(JSON.stringify({ id: "11111111-1111-4111-8111-111111111111", sessionId: "33333333-3333-4333-8333-333333333333", createdAt: new Date().toISOString(), historyUrl: "/Translations/33333333-3333-4333-8333-333333333333" }));
      return;
    }
    response.setHeader("Content-Type", "text/html");
    if (options.pageCsp) response.setHeader("Content-Security-Policy", options.pageCsp);
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
