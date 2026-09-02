import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupCssPath = new URL("../popup/popup.css", import.meta.url);
const overlayPath = new URL("../content/translator.js", import.meta.url);
const iconPath = new URL("../icons/source.svg", import.meta.url);

test("popup and overlay use the canonical Glosify palette", async () => {
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

test("extension icon uses Glosify midnight and green instead of the legacy purple", async () => {
  const icon = await readFile(iconPath, "utf8");

  assert.match(icon, /#041329/iu);
  assert.match(icon, /#53E076/iu);
  assert.doesNotMatch(icon, /#6558e8/iu);
});
