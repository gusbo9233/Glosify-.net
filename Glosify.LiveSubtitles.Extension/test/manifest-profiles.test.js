import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

async function readManifest(name) {
  return JSON.parse(await readFile(new URL(`../manifest.${name}.json`, import.meta.url), "utf8"));
}

async function readConfig(name) {
  return (await import(`../config.${name}.js`)).CONFIG;
}

test("Store manifest omits the development key", async () => {
  const base = await readManifest("base");
  const store = await readManifest("store");

  assert.equal("key" in base, false);
  assert.equal("key" in store, false);
  assert.equal("key" in { ...base, ...store }, false);
});

test("navigation lifecycle uses the committed-document permission", async () => {
  const base = await readManifest("base");

  assert.ok(base.permissions.includes("webNavigation"));
});

test("development and test profiles retain the stable unpacked extension ID", async () => {
  const development = await readManifest("development");
  const browserTest = await readManifest("test");
  const tabCaptureTest = await readManifest("test-tab");

  assert.equal(development.key, browserTest.key);
  assert.equal(browserTest.key, tabCaptureTest.key);
  assert.equal(extensionIdForKey(development.key), "akepdpjieiokffdapibipomhbplikock");
});

test("test hooks and audio capture mode are independent profile settings", async () => {
  assert.deepEqual(await readConfig("development"), {
    glosifyBaseUrl: "https://localhost:7032",
    testHooksEnabled: false,
    captureMode: "tab",
    allowInsecureRelay: false,
  });
  assert.deepEqual(await readConfig("store"), {
    glosifyBaseUrl: "https://glosify.se",
    testHooksEnabled: false,
    captureMode: "tab",
    allowInsecureRelay: false,
  });
  assert.equal((await readConfig("test")).captureMode, "synthetic");
  assert.equal((await readConfig("test-tab")).captureMode, "tab");
  assert.equal((await readConfig("test-tab")).testHooksEnabled, true);
});

test("real tab capture has a test-only browser-scoped keyboard command", async () => {
  const tabCaptureTest = await readManifest("test-tab");

  assert.deepEqual(tabCaptureTest.commands["test-start-tab-capture"], {
    suggested_key: {
      default: "Ctrl+Shift+8",
      mac: "Command+Shift+8",
    },
    description: "Start the real tab-capture browser test",
  });
});

function extensionIdForKey(key) {
  const hex = createHash("sha256")
    .update(Buffer.from(key, "base64"))
    .digest("hex")
    .slice(0, 32);
  return [...hex]
    .map(character => String.fromCharCode(97 + Number.parseInt(character, 16)))
    .join("");
}
