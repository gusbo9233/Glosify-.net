import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const workerPath = new URL("../background/service-worker.js", import.meta.url);

test("overlay settings persist without an in-memory catalog", async () => {
  const source = await readFile(workerPath, "utf8");

  assert.doesNotMatch(source, /if \(!state\.catalog\) return;/u);
  assert.match(
    source,
    /TranslatorState\.normalizeSettingsForStorage\(settings, state\.catalog\)/u);
  assert.match(source, /await persistSettings\(\);\s*broadcastState\(\);/u);
});
