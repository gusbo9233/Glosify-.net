import { test, expect, chromium } from "@playwright/test";
import { execFile } from "node:child_process";
import { cp, mkdtemp, rm, writeFile } from "node:fs/promises";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import { WebSocketServer } from "ws";

const artifactsRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)), "../artifacts");
const sessionId = "11111111-1111-4111-8111-111111111111";
const extensionId = "akepdpjieiokffdapibipomhbplikock";
const execFileAsync = promisify(execFile);

test("same-document navigation continues and full navigation stops capture", async () => {
  const harness = await launchHarness({
    storage: { glosifyTransparentSubtitles: true },
  });
  try {
    const page = await openAudioPage(harness);
    await page.getByRole("button", { name: "Play synthetic speech" }).click();
    await page.bringToFront();
    const started = await harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "test:start",
    }));
    expect(started.ok, JSON.stringify(started)).toBe(true);
    expect(started.result.transparentSubtitles).toBe(true);

    await expect.poll(() => extensionState(harness.worker)).toMatchObject({
      active: true,
      status: "subtitling",
      currentMinute: 1,
    });
    await expect.poll(() => harness.mock.audioMessages).toBeGreaterThan(0);
    await expect(page.locator("#glosify-live-subtitles-host")).toHaveCount(1);
    await expect.poll(() => overlayState(harness.control)).toMatchObject({
      installed: true,
      transparentSubtitles: true,
    });

    const updated = await harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "popup:set-transparent-subtitles",
      enabled: false,
    }));
    expect(updated.ok, JSON.stringify(updated)).toBe(true);
    expect(updated.result.transparentSubtitles).toBe(false);
    await expect.poll(() => overlayState(harness.control)).toMatchObject({
      transparentSubtitles: false,
    });
    await expect.poll(() => harness.worker.evaluate(async () => (
      await chrome.storage.local.get("glosifyTransparentSubtitles")
    ).glosifyTransparentSubtitles)).toBe(false);

    await page.evaluate(() => history.pushState({}, "", "/audio/episode/2"));
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({
      active: true,
      status: "subtitling",
    });

    await page.goto(`${harness.mock.baseUrl}/next`);
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({ active: false });
    await expect.poll(() => harness.mock.deletedSessions).toBe(1);
    await expect.poll(() => harness.mock.drainRequests).toBe(1);
  } finally {
    await harness.close();
  }
});

test("cross-origin full navigation stops capture", async () => {
  const harness = await launchHarness();
  try {
    const page = await openAudioPage(harness);
    await page.getByRole("button", { name: "Play synthetic speech" }).click();
    await page.bringToFront();
    const started = await harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "test:start",
    }));
    expect(started.ok, JSON.stringify(started)).toBe(true);
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({ active: true });

    await page.goto(`${harness.mock.alternateBaseUrl}/next`);
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({ active: false });
    await expect.poll(() => harness.mock.deletedSessions).toBe(1);
    await expect.poll(() => harness.mock.drainRequests).toBe(1);
  } finally {
    await harness.close();
  }
});

test("concurrent starts share refresh and survive five-second session creation", async () => {
  const harness = await launchHarness({ createDelayMs: 5_000 });
  try {
    const page = await openAudioPage(harness);
    await page.bringToFront();
    await restoreTestState(harness.control);

    const results = await harness.control.evaluate(() => Promise.all([
      chrome.runtime.sendMessage({ type: "popup:start" }),
      chrome.runtime.sendMessage({ type: "popup:start" }),
    ]));
    expect(results.every(result => result.ok), JSON.stringify(results)).toBe(true);
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({ active: true });
    expect(harness.mock.refreshRequests).toBe(1);
    expect(harness.mock.createdSessions).toBe(1);
    await expect.poll(() => harness.mock.audioMessages).toBeGreaterThan(0);

    const stopped = await harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "overlay:stop",
    }));
    expect(stopped.ok, JSON.stringify(stopped)).toBe(true);
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({ active: false });
    await expect.poll(() => harness.mock.deletedSessions).toBe(1);
    await expect.poll(() => harness.mock.drainRequests).toBe(1);
  } finally {
    await harness.close();
  }
});

test("stopping during session creation cleans up and permits a later start", async () => {
  const harness = await launchHarness({ createDelayMs: 1_000 });
  try {
    const page = await openAudioPage(harness);
    await page.bringToFront();
    await restoreTestState(harness.control);

    const starting = harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "popup:start",
    }));
    await expect.poll(() => harness.mock.createdSessions).toBe(1);
    const stopped = await harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "overlay:stop",
    }));
    expect(stopped.ok, JSON.stringify(stopped)).toBe(true);
    expect((await starting).ok).toBe(true);
    await expect.poll(() => harness.mock.deletedSessions).toBe(1);
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({ active: false });

    const restarted = await harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "popup:start",
    }));
    expect(restarted.ok, JSON.stringify(restarted)).toBe(true);
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({ active: true });
    expect(harness.mock.createdSessions).toBe(2);

    const finalStop = await harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "overlay:stop",
    }));
    expect(finalStop.ok, JSON.stringify(finalStop)).toBe(true);
    await expect.poll(() => harness.mock.deletedSessions).toBe(2);
  } finally {
    await harness.close();
  }
});

test("tab-capture profile streams real tab audio and renders the final caption", async () => {
  test.skip(process.platform !== "linux", "Real tab capture requires native X11 input; Playwright virtual keyboard input cannot activate browser-scoped chrome.commands shortcuts.");
  const finalCaption = "Real tab final caption";
  const harness = await launchHarness({ captureMode: "tab", finalCaption });
  try {
    const page = await openAudioPage(harness);
    await page.getByRole("button", { name: "Play synthetic speech" }).click();
    await restoreTestState(harness.control);
    const targetTabId = await tabIdForUrl(harness.control, page.url());
    const command = await harness.worker.evaluate(async () => (
      await chrome.commands.getAll()
    ).find(item => item.name === "test-start-tab-capture"));
    expect(command?.shortcut).toBe("Ctrl+Shift+8");
    await expect.poll(() => tabIsAudible(harness.worker, targetTabId)).toBe(true);

    await page.bringToFront();
    await pressNativeTabCaptureShortcut("Glosify tab capture test");

    await expect.poll(() => extensionState(harness.worker)).toMatchObject({
      active: true,
      status: "subtitling",
      currentMinute: 1,
    });
    await expect.poll(() => capturedTabStatus(harness.worker, targetTabId)).toBe("active");
    await expect.poll(() => harness.mock.audioMessages).toBeGreaterThan(0);
    await expect.poll(() => overlayState(harness.control)).toMatchObject({
      activeSessionId: sessionId,
      captionText: finalCaption,
      installed: true,
    });

    const stopped = await harness.control.evaluate(() => chrome.runtime.sendMessage({
      type: "overlay:stop",
    }));
    expect(stopped.ok, JSON.stringify(stopped)).toBe(true);
    await expect.poll(() => extensionState(harness.worker)).toMatchObject({ active: false });
    await expect.poll(() => harness.mock.deletedSessions).toBe(1);
    await expect.poll(() => harness.mock.drainRequests).toBe(1);
    await expect.poll(() => capturedTabStatus(harness.worker, targetTabId)).not.toBe("active");
  } finally {
    await harness.close();
  }
});

async function launchHarness({
  captureMode = "synthetic",
  createDelayMs = 0,
  finalCaption = "Synthetic final caption",
  storage = {},
} = {}) {
  const temporaryRoot = await mkdtemp(path.join(os.tmpdir(), "glosify-extension-test-"));
  let context = null;
  let mock = null;
  try {
    mock = await test.step("start isolated mock backend", () => (
      startMockGlosify({ createDelayMs, finalCaption })));
    const builtProfile = captureMode === "tab" ? "test-tab" : "test";
    const extensionRoot = path.join(temporaryRoot, "extension");
    await test.step("generate isolated extension profile", async () => {
      await cp(path.join(artifactsRoot, builtProfile), extensionRoot, { recursive: true });
      await writeFile(path.join(extensionRoot, "config.js"), `export const CONFIG = Object.freeze(${JSON.stringify({
        glosifyBaseUrl: mock.baseUrl,
        testHooksEnabled: true,
        captureMode,
        allowInsecureRelay: true,
      }, null, 2)});\n`);
    });

    context = await test.step("launch Chromium with the generated extension", () => (
      chromium.launchPersistentContext(path.join(temporaryRoot, "profile"), {
        headless: false,
        ignoreDefaultArgs: captureMode === "tab" ? ["--mute-audio"] : undefined,
        args: [
          `--disable-extensions-except=${extensionRoot}`,
          `--load-extension=${extensionRoot}`,
          "--autoplay-policy=no-user-gesture-required",
          "--use-fake-ui-for-media-stream",
        ],
      })));
    const worker = context.serviceWorkers()[0]
      ?? await test.step("wait for the extension service worker", () => (
        context.waitForEvent("serviceworker", { timeout: 10_000 })));
    expect(worker.url()).toBe(
      `chrome-extension://${extensionId}/background/service-worker.js`);
    await worker.evaluate(values => chrome.storage.local.set(values), {
      glosifyRefreshToken: "refresh-token",
      ...storage,
    });
    const control = context.pages()[0] ?? await context.newPage();
    await control.goto(`chrome-extension://${extensionId}/popup/popup.html`);
    let closed = false;
    return {
      close: async () => {
        if (closed) {
          return;
        }
        closed = true;
        await context.close();
        await mock.close();
        await rm(temporaryRoot, { recursive: true, force: true });
      },
      context,
      control,
      mock,
      worker,
    };
  } catch (error) {
    await context?.close();
    await mock?.close();
    await rm(temporaryRoot, { recursive: true, force: true });
    throw error;
  }
}

async function pressNativeTabCaptureShortcut(windowTitle) {
  if (!process.env.DISPLAY) {
    throw new Error("The real tab-capture gate requires an X11 DISPLAY.");
  }
  let browserWindows;
  try {
    ({ stdout: browserWindows } = await execFileAsync("xdotool", [
      "search",
      "--onlyvisible",
      "--name",
      windowTitle,
    ], { encoding: "utf8" }));
  } catch (error) {
    throw new Error(
      "The real tab-capture gate requires xdotool and a visible Chromium X11 window.",
      { cause: error });
  }
  const windowId = browserWindows.trim().split(/\s+/u).at(-1);
  if (!/^\d+$/u.test(windowId)) {
    throw new Error("xdotool did not return a visible Chromium X11 window.");
  }
  await execFileAsync(
    "xdotool",
    ["windowfocus", "--sync", windowId],
    { timeout: 10_000 });
  await execFileAsync("xdotool", [
    "key",
    "--window",
    windowId,
    "--clearmodifiers",
    "ctrl+shift+8",
  ], { timeout: 10_000 });
}

async function openAudioPage(harness) {
  const page = await harness.context.newPage();
  await page.goto(`${harness.mock.baseUrl}/audio`);
  return page;
}

async function restoreTestState(control) {
  const restored = await control.evaluate(() => chrome.runtime.sendMessage({
    type: "test:restore-local-state",
  }));
  expect(restored.ok, JSON.stringify(restored)).toBe(true);
}

async function tabIdForUrl(control, url) {
  return control.evaluate(async expectedUrl => {
    const tabs = await chrome.tabs.query({ url: expectedUrl });
    if (tabs.length !== 1 || !tabs[0].id) {
      throw new Error(`Expected one browser-test tab for ${expectedUrl}; found ${tabs.length}.`);
    }
    return tabs[0].id;
  }, url);
}

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

async function capturedTabStatus(worker, tabId) {
  return worker.evaluate(async expectedTabId => {
    const capturedTabs = await chrome.tabCapture.getCapturedTabs();
    return capturedTabs.find(tab => tab.tabId === expectedTabId)?.status ?? null;
  }, tabId);
}

async function tabIsAudible(worker, tabId) {
  return worker.evaluate(async expectedTabId => (
    await chrome.tabs.get(expectedTabId)
  ).audible === true, tabId);
}

async function overlayState(control) {
  return control.evaluate(async () => {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    return chrome.tabs.sendMessage(tab.id, { type: "overlay:get-state" });
  });
}

async function startMockGlosify({ createDelayMs = 0, finalCaption } = {}) {
  let startedAtUtc = null;
  const state = {
    audioMessages: 0,
    deletedSessions: 0,
    refreshRequests: 0,
    createdSessions: 0,
    drainRequests: 0,
  };
  const requestHandler = async (request, response) => {
    const url = new URL(request.url, "http://127.0.0.1");
    if (url.pathname === "/audio" || url.pathname.startsWith("/audio/") || url.pathname === "/next") {
      response.writeHead(200, { "Content-Type": "text/html" });
      response.end(`<!doctype html><title>Glosify tab capture test</title><button>Play synthetic speech</button><script>
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
  };

  const server = http.createServer(requestHandler);
  const alternateServer = http.createServer(requestHandler);
  const sockets = new WebSocketServer({
    server,
    handleProtocols: protocols => protocols.has("glosify-realtime")
      ? "glosify-realtime"
      : false,
  });
  sockets.on("connection", socket => {
    socket.send(JSON.stringify({ type: "glosify.relay.ready" }));
    socket.on("message", raw => {
      const message = JSON.parse(raw.toString());
      if (message.type === "glosify.relay.close") {
        state.drainRequests += 1;
        socket.send(JSON.stringify({
          type: "response.text.done",
          response_id: "shutdown-final",
          text: "Shutdown final caption",
        }));
        socket.send(JSON.stringify({ type: "glosify.relay.closed" }));
        return;
      }
      state.audioMessages += 1;
      if (state.audioMessages === 1) {
        socket.send(JSON.stringify({
          type: "response.text.done",
          response_id: "final-only",
          text: finalCaption,
        }));
      }
    });
  });

  await listenOnLoopback(server);
  await listenOnLoopback(alternateServer);
  return Object.assign(state, {
    baseUrl: loopbackUrl(server),
    alternateBaseUrl: loopbackUrl(alternateServer),
    close: async () => {
      await new Promise(resolve => sockets.close(resolve));
      await Promise.all([closeServer(server), closeServer(alternateServer)]);
    },
  });
}

function listenOnLoopback(server) {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
}

function loopbackUrl(server) {
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("The mock server did not bind to a TCP port.");
  }
  return `http://127.0.0.1:${address.port}`;
}

function closeServer(server) {
  return new Promise((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve());
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
