import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

async function readManifest(name) {
  return JSON.parse(await readFile(new URL(`../manifest.${name}.json`, import.meta.url), "utf8"));
}

test("Store manifest omits the development key", async () => {
  const base = await readManifest("base");
  const store = await readManifest("store");

  assert.equal("key" in base, false);
  assert.equal("key" in store, false);
  assert.equal("key" in { ...base, ...store }, false);
});

test("development and test profiles retain the stable unpacked extension ID", async () => {
  const development = await readManifest("development");
  const browserTest = await readManifest("test");

  assert.equal(development.key, browserTest.key);
  assert.equal(extensionIdForKey(development.key), "akepdpjieiokffdapibipomhbplikock");
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
