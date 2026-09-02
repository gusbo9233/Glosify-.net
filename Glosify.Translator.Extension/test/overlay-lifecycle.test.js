import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const contentPath = new URL("../content/translator.js", import.meta.url);

test("overlay close unregisters its runtime listener", async () => {
  const source = await readFile(contentPath, "utf8");

  assert.match(source, /onMessage\.addListener\(handleRuntimeMessage\)/u);
  assert.match(source, /onMessage\.removeListener\(handleRuntimeMessage\)/u);
});

test("source input stays local while preferences are debounced", async () => {
  const source = await readFile(contentPath, "utf8");

  assert.match(source, /sourceText\.addEventListener\("input", \(\) => changed\(\)\)/u);
  assert.match(source, /preferences\.addEventListener\("input", \(\) => changed\("debounced"\)\)/u);
  assert.match(source, /setTimeout\(persistSettings, SETTINGS_SAVE_DELAY_MS\)/u);
});
