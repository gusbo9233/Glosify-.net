import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const contentPath = new URL("../content/subtitles.js", import.meta.url);
const workerPath = new URL("../background/service-worker.js", import.meta.url);

test("subtitle window offers an accessible stop action", async () => {
  const source = await readFile(contentPath, "utf8");

  assert.match(source, /<button class="action stop" type="button" title="Stop live subtitles">Stop<\/button>/);
  assert.match(source, /stopButton\.addEventListener\("click", stopSubtitles\)/);
  assert.match(source, /chrome\.runtime\.sendMessage\(\{ type: "overlay:stop" \}\)/);
  assert.match(source, /stopButton\.disabled = true;[\s\S]*stopButton\.disabled = false;/);
});

test("overlay stop uses the same worker stop path as the popup", async () => {
  const source = await readFile(workerPath, "utf8");

  assert.match(source, /case "popup:stop":\s*case "overlay:stop":\s*await stopSession\(null, "ready"\);/);
});
