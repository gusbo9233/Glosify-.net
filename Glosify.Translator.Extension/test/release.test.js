import assert from "node:assert/strict";
import { cp, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { validateStore } from "../scripts/validate-store.mjs";
import { checkProduction, storeCallback } from "../scripts/check-production.mjs";

const root = fileURLToPath(new URL("..", import.meta.url));
execFileSync(process.execPath, ["scripts/build.mjs", "store"], { cwd: root });

test("the packaged Store build passes release policy", async () => {
  await validateStore(path.join(root, "artifacts/store"));
});

for (const [name, mutate] of [
  ["development URL", async dir => writeFile(path.join(dir, "config.js"), 'export const CONFIG = { glosifyBaseUrl: "https://localhost:7032", testHooksEnabled: false };')],
  ["enabled test hooks", async dir => {
    const config = await readFile(path.join(dir, "config.js"), "utf8");
    await writeFile(path.join(dir, "config.js"), config.replace("false", "true"));
  }],
  ["extra runtime file", async dir => writeFile(path.join(dir, "remote-loader.js"), "")],
  ["unsafe CSP", async dir => changeManifest(dir, manifest => { manifest.content_security_policy.extension_pages += "; script-src 'unsafe-eval'"; })],
  ["persistent content script", async dir => changeManifest(dir, manifest => { manifest.content_scripts = [{ matches: ["<all_urls>"], js: ["content/translator.js"] }]; })],
  ["external message access", async dir => changeManifest(dir, manifest => { manifest.externally_connectable = { matches: ["https://*/*"] }; })],
  ["broad host access", async dir => changeManifest(dir, manifest => { manifest.host_permissions = ["<all_urls>"]; })],
  ["development key", async dir => changeManifest(dir, manifest => { manifest.key = "dev"; })],
]) {
  test(`release validation rejects ${name}`, async () => {
    const temporary = await mkdtemp(path.join(os.tmpdir(), "translator-release-"));
    try {
      await cp(path.join(root, "artifacts/store"), temporary, { recursive: true });
      await mutate(temporary);
      await assert.rejects(validateStore(temporary));
    } finally { await rm(temporary, { recursive: true, force: true }); }
  });
}

async function changeManifest(directory, mutate) {
  const file = path.join(directory, "manifest.json");
  const manifest = JSON.parse(await readFile(file, "utf8"));
  mutate(manifest);
  await writeFile(file, JSON.stringify(manifest));
}

test("callback instructions accept only a Store-shaped ID, never a local ID or wildcard", () => {
  assert.equal(storeCallback("abcdefghijklmnopabcdefghijklmnop"), "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/glosify");
  for (const id of [undefined, "", "*", "gogoghheeaelbddlgiehjdpmnnpnpjfk", "https://evil.test", "z".repeat(32), "a".repeat(31)]) {
    assert.throws(() => storeCallback(id));
  }
});

test("read-only production checks reject login redirects and outdated privacy", async () => {
  const requested = [];
  const failures = await checkProduction(async (url, options) => {
    requested.push(url);
    assert.equal(options.redirect, "manual");
    assert.equal(options.credentials, "omit");
    assert.equal(options.body, undefined);
    if (url.endsWith("/api/me")) return new Response(null, { status: 401 });
    if (url.endsWith("/catalog")) return new Response(null, { status: 302 });
    return new Response("<h1>Old policy</h1>", { headers: { "content-type": "text/html" } });
  });
  assert.equal(requested.length, 3);
  assert.equal(failures.length, 2);
  assert.match(failures[0], /catalog.*302/u);
  assert.match(failures[1], /privacy/u);
});

test("read-only production checks pass compatible APIs and the public policy", async () => {
  const failures = await checkProduction(async url => url.includes("/api/")
    ? new Response(null, { status: 401 })
    : new Response("Translator data: Chrome Web Store Limited Use", { headers: { "content-type": "text/html" } }));
  assert.deepEqual(failures, []);
});
