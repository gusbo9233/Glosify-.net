import { test, expect, chromium } from "@playwright/test";
import { mkdtemp, rm } from "node:fs/promises";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocketServer } from "ws";

const extensionRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)), "../artifacts/test");
const sessionId = "11111111-1111-4111-8111-111111111111";
const extensionId = "akepdpjieiokffdapibipomhbplikock";

test("same-document navigation continues and full navigation stops capture", async () => {
  const mock = await startMockGlosify();
  const profile = await mkdtemp(path.join(os.tmpdir(), "glosify-extension-test-"));
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [
      `--disable-extensions-except=${extensionRoot}`,
      `--load-extension=${extensionRoot}`,
      "--autoplay-policy=no-user-gesture-required",
      "--use-fake-ui-for-media-stream",
    ],
  });
  try {
    const worker = context.serviceWorkers()[0]
      ?? await context.waitForEvent("serviceworker");
    expect(worker.url()).toBe(`chrome-extension://${extensionId}/background/service-worker.js`);
    await worker.evaluate(() => chrome.storage.local.set({
      glosifyRefreshToken: "refresh-token",
      glosifyTransparentSubtitles: true,
    }));

    const control = context.pages()[0] ?? await context.newPage();
    await control.goto(`chrome-extension://${extensionId}/popup/popup.html`);
    const page = await context.newPage();
    await page.goto("http://127.0.0.1:4173/audio");
    await page.getByRole("button", { name: "Play synthetic speech" }).click();
    await page.bringToFront();
    const started = await control.evaluate(() => chrome.runtime.sendMessage({ type: "test:start" }));
    expect(started.ok, JSON.stringify(started)).toBe(true);
    expect(started.result.transparentSubtitles).toBe(true);

    await expect.poll(() => extensionState(worker)).toMatchObject({
      active: true,
      status: "subtitling",
      currentMinute: 1,
    });
    await expect.poll(() => mock.audioMessages).toBeGreaterThan(0);
    await expect(page.locator("#glosify-live-subtitles-host")).toHaveCount(1);
    await expect.poll(() => overlayState(control)).toMatchObject({
      installed: true,
      transparentSubtitles: true,
    });

    const updated = await control.evaluate(() => chrome.runtime.sendMessage({
      type: "popup:set-transparent-subtitles",
      enabled: false,
    }));
    expect(updated.ok, JSON.stringify(updated)).toBe(true);
    expect(updated.result.transparentSubtitles).toBe(false);
    await expect.poll(() => overlayState(control)).toMatchObject({ transparentSubtitles: false });
    await expect.poll(() => worker.evaluate(async () => (
      await chrome.storage.local.get("glosifyTransparentSubtitles")
    ).glosifyTransparentSubtitles)).toBe(false);

    await page.evaluate(() => history.pushState({}, "", "/audio/episode/2"));
    await expect.poll(() => extensionState(worker)).toMatchObject({
      active: true,
      status: "subtitling",
    });

    await page.goto("http://127.0.0.1:4173/next");
    await expect.poll(() => extensionState(worker)).toMatchObject({ active: false });
    await expect.poll(() => mock.deletedSessions).toBe(1);
  } finally {
    await context.close();
    await mock.close();
    await rm(profile, { recursive: true, force: true });
  }
});

test("cross-origin full navigation stops capture", async () => {
  const mock = await startMockGlosify();
  const profile = await mkdtemp(path.join(os.tmpdir(), "glosify-extension-test-"));
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [
      `--disable-extensions-except=${extensionRoot}`,
      `--load-extension=${extensionRoot}`,
      "--autoplay-policy=no-user-gesture-required",
      "--use-fake-ui-for-media-stream",
    ],
  });
  try {
    const worker = context.serviceWorkers()[0]
      ?? await context.waitForEvent("serviceworker");
    await worker.evaluate(() => chrome.storage.local.set({ glosifyRefreshToken: "refresh-token" }));
    const control = context.pages()[0] ?? await context.newPage();
    await control.goto(`chrome-extension://${extensionId}/popup/popup.html`);
    const page = await context.newPage();
    await page.goto("http://127.0.0.1:4173/audio");
    await page.getByRole("button", { name: "Play synthetic speech" }).click();
    await page.bringToFront();
    const started = await control.evaluate(() => chrome.runtime.sendMessage({ type: "test:start" }));
    expect(started.ok, JSON.stringify(started)).toBe(true);
    await expect.poll(() => extensionState(worker)).toMatchObject({ active: true });

    await page.goto("http://localhost:4173/next");
    await expect.poll(() => extensionState(worker)).toMatchObject({ active: false });
    await expect.poll(() => mock.deletedSessions).toBe(1);
  } finally {
    await context.close();
    await mock.close();
    await rm(profile, { recursive: true, force: true });
  }
});

test("concurrent starts share refresh and survive five-second session creation", async () => {
  const mock = await startMockGlosify({ createDelayMs: 5_000 });
  const profile = await mkdtemp(path.join(os.tmpdir(), "glosify-extension-test-"));
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [
      `--disable-extensions-except=${extensionRoot}`,
      `--load-extension=${extensionRoot}`,
      "--autoplay-policy=no-user-gesture-required",
      "--use-fake-ui-for-media-stream",
    ],
  });
  try {
    const worker = context.serviceWorkers()[0]
      ?? await context.waitForEvent("serviceworker");
    await worker.evaluate(() => chrome.storage.local.set({ glosifyRefreshToken: "refresh-token" }));
    const control = context.pages()[0] ?? await context.newPage();
    await control.goto(`chrome-extension://${extensionId}/popup/popup.html`);
    const page = await context.newPage();
    await page.goto("http://127.0.0.1:4173/audio");
    await page.bringToFront();

    const restored = await control.evaluate(() => chrome.runtime.sendMessage({
      type: "test:restore-local-state",
    }));
    expect(restored.ok, JSON.stringify(restored)).toBe(true);

    const results = await control.evaluate(() => Promise.all([
      chrome.runtime.sendMessage({ type: "popup:start" }),
      chrome.runtime.sendMessage({ type: "popup:start" }),
    ]));
    expect(results.every(result => result.ok), JSON.stringify(results)).toBe(true);
    await expect.poll(() => extensionState(worker)).toMatchObject({ active: true });
    expect(mock.refreshRequests).toBe(1);
    expect(mock.createdSessions).toBe(1);
    await expect.poll(() => mock.audioMessages).toBeGreaterThan(0);
  } finally {
    await context.close();
    await mock.close();
    await rm(profile, { recursive: true, force: true });
  }
});

async function extensionState(worker) {
  return worker.evaluate(async () => {
    const { glosifyActiveSession: active } = await chrome.storage.session.get(
      "glosifyActiveSession");
    return {
      active: Boolean(active?.sessionId),
      status: active?.status ?? "ready",
      currentMinute: active?.currentMinute ?? 0,
    };
  });
}

async function overlayState(control) {
  return control.evaluate(async () => {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    return chrome.tabs.sendMessage(tab.id, { type: "overlay:get-state" });
  });
}

async function startMockGlosify({ createDelayMs = 0 } = {}) {
  let startedAtUtc = null;
  const state = {
    audioMessages: 0,
    deletedSessions: 0,
    refreshRequests: 0,
    createdSessions: 0,
  };
  const server = http.createServer(async (request, response) => {
    const url = new URL(request.url, "http://127.0.0.1:4173");
    if (url.pathname === "/audio" || url.pathname.startsWith("/audio/") || url.pathname === "/next") {
      response.writeHead(200, { "Content-Type": "text/html" });
      response.end(`<!doctype html><button>Play synthetic speech</button><script>
        document.querySelector('button').onclick = async () => {
          const context = new AudioContext();
          const oscillator = context.createOscillator();
          oscillator.frequency.value = 220;
          oscillator.connect(context.destination);
          oscillator.start();
          await context.resume();
        };
      </script>`);
      return;
    }
    if (url.pathname === "/api/auth/refresh" && request.method === "POST") {
      state.refreshRequests += 1;
      return json(response, {
        accessToken: "access-token",
        refreshToken: "rotated-refresh-token",
        expiresIn: 3600,
      });
    }
    if (url.pathname === "/api/me") {
      return json(response, { email: "extension@example.test", availableCredits: 100 });
    }
    if (url.pathname === "/api/service-status/paid-features") {
      return json(response, { available: true });
    }
    if (url.pathname === "/api/realtime-translation/catalog") {
      return json(response, {
        availableCredits: 100,
        maxSessionMinutes: 30,
        renewalLeadSeconds: 5,
        heartbeatSeconds: 15,
        savedSourceTranscriptsEnabled: true,
        languages: [{ code: "en", name: "English" }],
        modes: [{ code: "scribe", name: "Scribe", description: "Test", creditsPerMinute: 8 }],
        sourceLanguages: [{ code: "auto", name: "Auto detect" }],
        quizLanguages: [{ code: "de", name: "German" }],
        selectedQuizLanguage: { code: "de", name: "German" },
      });
    }
    if (url.pathname === "/api/realtime-translation/sessions" && request.method === "POST") {
      state.createdSessions += 1;
      if (createDelayMs > 0) {
        await new Promise(resolve => setTimeout(resolve, createDelayMs));
      }
      return json(response, {
        sessionId,
        availableCredits: 100,
        relayToken: "A".repeat(43),
        relayPath: `/api/realtime-translation/sessions/${sessionId}/stream`,
      });
    }
    if (url.pathname.endsWith("/minutes/1/begin") && request.method === "POST") {
      startedAtUtc = new Date();
      return json(response, minuteResult(startedAtUtc, 1));
    }
    if (url.pathname.endsWith("/heartbeat") && request.method === "POST") {
      return json(response, {
        sessionId,
        status: "active",
        chargedMinutes: 1,
        creditsCharged: 8,
        sessionStartedAtUtc: startedAtUtc?.toISOString(),
        audioSendAuthorizedUntilUtc: startedAtUtc
          ? new Date(startedAtUtc.getTime() + 60_000).toISOString()
          : null,
        serverNowUtc: new Date().toISOString(),
      });
    }
    if (url.pathname === `/api/realtime-translation/sessions/${sessionId}`
        && request.method === "DELETE") {
      state.deletedSessions += 1;
      response.writeHead(204).end();
      return;
    }
    response.writeHead(404).end();
  });
  const sockets = new WebSocketServer({
    server,
    handleProtocols: protocols => protocols.has("glosify-realtime")
      ? "glosify-realtime"
      : false,
  });
  sockets.on("connection", socket => {
    socket.send(JSON.stringify({ type: "glosify.relay.ready" }));
    socket.on("message", () => {
      state.audioMessages += 1;
      if (state.audioMessages === 1) {
        socket.send(JSON.stringify({
          type: "response.text.done",
          response_id: "final-only",
          text: "Synthetic final caption",
        }));
      }
    });
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(4173, "0.0.0.0", resolve);
  });
  return Object.assign(state, {
    close: () => new Promise(resolve => sockets.close(() => server.close(resolve))),
  });
}

function minuteResult(startedAt, minuteIndex) {
  return {
    sessionId,
    minuteIndex,
    status: "begun",
    availableCredits: 92,
    chargedMinutes: minuteIndex,
    creditsCharged: 8 * minuteIndex,
    sessionStartedAtUtc: startedAt.toISOString(),
    audioSendAuthorizedUntilUtc: new Date(
      startedAt.getTime() + minuteIndex * 60_000).toISOString(),
    serverNowUtc: new Date().toISOString(),
  };
}

function json(response, body) {
  response.writeHead(200, {
    "Content-Type": "application/json",
    "Access-Control-Allow-Origin": "*",
  });
  response.end(JSON.stringify(body));
}
