import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("base manifest has the minimal translator permissions", async () => {
  const manifest = JSON.parse(await readFile(new URL("../manifest.base.json", import.meta.url)));
  assert.deepEqual([...manifest.permissions].sort(), ["activeTab", "clipboardWrite", "identity", "scripting", "storage"]);
  assert.equal(manifest.content_scripts, undefined);
  assert.deepEqual(manifest.web_accessible_resources, [{
    resources: ["overlay/translator.html"],
    matches: ["http://*/*", "https://*/*"],
  }]);
});

test("store manifest has production-only host access", async () => {
  const manifest = JSON.parse(await readFile(new URL("../manifest.store.json", import.meta.url)));
  assert.deepEqual(manifest.host_permissions, ["https://glosify.se/*"]);
  assert.equal(manifest.key, undefined);
});
