import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupCssPath = new URL("../popup/popup.css", import.meta.url);
const overlayPath = new URL("../overlay/translator.css", import.meta.url);

test("popup and overlay use the canonical GlobeGlotter palette", async () => {
  const [popupCss, overlay] = await Promise.all([
    readFile(popupCssPath, "utf8"),
    readFile(overlayPath, "utf8"),
  ]);

  for (const source of [popupCss, overlay]) {
    assert.match(source, /#53e076/iu);
    assert.match(source, /#041329/iu);
    assert.match(source, /#d6e3ff/iu);
    assert.doesNotMatch(source, /#6558e8/iu);
  }
});
