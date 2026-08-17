import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import "../lib/subtitle-appearance.js";

const { applyTransparentSubtitles, normalizeTransparentSubtitles } =
  globalThis.GlosifySubtitleAppearance;

test("transparent subtitles default off and require an explicit true value", () => {
  assert.equal(normalizeTransparentSubtitles(undefined), false);
  assert.equal(normalizeTransparentSubtitles(false), false);
  assert.equal(normalizeTransparentSubtitles("true"), false);
  assert.equal(normalizeTransparentSubtitles(true), true);
});

test("appearance preference toggles the transparent panel class", () => {
  const classes = new Set(["panel"]);
  const panel = {
    classList: {
      toggle(name, force) {
        if (force) {
          classes.add(name);
        } else {
          classes.delete(name);
        }
      },
    },
  };

  assert.equal(applyTransparentSubtitles(panel, true), true);
  assert.equal(classes.has("transparent"), true);
  assert.equal(applyTransparentSubtitles(panel, false), false);
  assert.equal(classes.has("transparent"), false);
});

test("transparent appearance keeps translation text and restores chrome on hover or focus", async () => {
  const source = await readFile(new URL("../content/subtitles.js", import.meta.url), "utf8");

  assert.match(source, /\.panel\.transparent:not\(:hover\):not\(:focus-within\)/);
  assert.match(source, /\.panel\.transparent[^{}]+\{\s*resize:\s*none;/s);
  assert.match(source, /\.header,[\s\S]*\.empty,[\s\S]*\.meta,[\s\S]*\.typing,[\s\S]*\.footer/);
  assert.match(source, /\.panel\.transparent[^{}]+\.translation\s*\{\s*text-shadow:/s);
  assert.doesNotMatch(source, /\.panel\.transparent[^{}]+\.translation\s*\{[^}]*opacity:\s*0/s);
});
