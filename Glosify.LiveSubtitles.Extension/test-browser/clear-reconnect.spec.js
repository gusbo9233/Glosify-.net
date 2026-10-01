import { test, expect, chromium } from "@playwright/test";
import { fileURLToPath } from "node:url";

test("Clear remains scoped to the bound relay session", async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    await page.goto("about:blank");
    await page.evaluate(() => {
      globalThis.chrome = { runtime: { onMessage: {
        addListener(listener) { globalThis.receiveOverlayMessage = listener; },
      } } };
      const attachShadow = Element.prototype.attachShadow;
      Element.prototype.attachShadow = function (options) {
        const shadow = attachShadow.call(this, options);
        globalThis.subtitleShadow = shadow;
        return shadow;
      };
    });
    for (const file of ["lib/chat-buffer.js", "lib/subtitle-appearance.js", "content/subtitles.js"]) {
      await page.addScriptTag({ path: fileURLToPath(new URL(`../${file}`, import.meta.url)) });
    }
    const send = message => page.evaluate(value => {
      let response;
      globalThis.receiveOverlayMessage(value, {}, result => { response = result; });
      return response;
    }, message);
    const caption = (sequence, delta) => send({ type: "overlay:subtitle", event: {
      stream: "translation", sequence, delta, replace: true, isFinal: true,
    } });
    await send({ type: "overlay:bind-session", sessionId: "old-relay" });
    await caption(40, "Old caption.");
    await page.evaluate(() => globalThis.subtitleShadow.querySelector(".clear").click());
    await send({ type: "overlay:bind-session", sessionId: "old-relay" });
    await caption(40, "Old caption.");
    expect((await send({ type: "overlay:get-state" })).captionText).toBe("");
    await caption(41, "Keep this history.");
    await send({ type: "overlay:bind-session", sessionId: "replacement-relay" });
    await caption(1, "First replacement caption.");
    expect((await send({ type: "overlay:get-state" })).captionText)
      .toBe("Keep this history.\nFirst replacement caption.");
  } finally {
    await browser.close();
  }
});
