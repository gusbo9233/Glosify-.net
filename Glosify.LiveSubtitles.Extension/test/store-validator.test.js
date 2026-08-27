import test from "node:test";
import assert from "node:assert/strict";
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { validateStore } from "../scripts/validate-store.mjs";

const extensionRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

test("accepts the generated Store profile", async t => {
  const store = await createStoreFixture(t);

  const result = await validateStore(store);

  assert.deepEqual(result.failures, []);
});

test("rejects localhost references in any packaged text file", async t => {
  const store = await createStoreFixture(t);
  await writeFile(
    path.join(store, "content/backend-notes"),
    "temporary endpoint: http://127.0.0.1:5000\n");

  const result = await validateStore(store);

  assert.ok(result.failures.includes(
    "localhost Store configuration in content/backend-notes"));
});

test("rejects remote scripts when src contains surrounding whitespace", async t => {
  const store = await createStoreFixture(t);
  await writeFile(
    path.join(store, "content/remote-script-fixture"),
    '<script src = "https://cdn.example.test/extension.js"></script>\n');

  const result = await validateStore(store);

  assert.ok(result.failures.includes(
    "remote or dynamic code in content/remote-script-fixture"));
});

async function createStoreFixture(t) {
  const temporaryRoot = await mkdtemp(path.join(os.tmpdir(), "glosify-store-validator-"));
  t.after(() => rm(temporaryRoot, { recursive: true, force: true }));
  const store = path.join(temporaryRoot, "store");
  await mkdir(store, { recursive: true });
  for (const directory of ["background", "content", "lib", "offscreen", "popup", "icons"]) {
    await cp(path.join(extensionRoot, directory), path.join(store, directory), { recursive: true });
  }
  await cp(path.join(extensionRoot, "config.store.js"), path.join(store, "config.js"));
  const base = JSON.parse(await readFile(path.join(extensionRoot, "manifest.base.json"), "utf8"));
  const overlay = JSON.parse(await readFile(path.join(extensionRoot, "manifest.store.json"), "utf8"));
  await writeFile(
    path.join(store, "manifest.json"),
    `${JSON.stringify({ ...base, ...overlay }, null, 2)}\n`);
  return store;
}
