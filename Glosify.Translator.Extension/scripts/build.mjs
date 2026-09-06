import { cp, mkdir, readFile, rm, utimes, writeFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const profile = process.argv[2];
if (!new Set(["development", "test", "store"]).has(profile)) {
  throw new Error("Build profile must be 'development', 'test', or 'store'.");
}
const output = path.join(root, "artifacts", profile);
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
for (const directory of ["background", "content", "icons", "lib", "overlay", "popup"]) {
  await cp(path.join(root, directory), path.join(output, directory), { recursive: true });
}
await cp(path.join(root, `config.${profile}.js`), path.join(output, "config.js"));
const { CONFIG } = await import(`../config.${profile}.js`);
const base = JSON.parse(await readFile(path.join(root, "manifest.base.json"), "utf8"));
const overlay = JSON.parse(await readFile(path.join(root, `manifest.${profile}.json`), "utf8"));
const manifest = {
  ...base, ...overlay,
  content_security_policy: {
    extension_pages: `default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; connect-src ${new URL(CONFIG.glosifyBaseUrl).origin}`,
  },
};
await writeFile(path.join(output, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
await normalizeTimes(output);
console.log(output);

async function normalizeTimes(directory) {
  const { readdir } = await import("node:fs/promises");
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) await normalizeTimes(target);
    await utimes(target, new Date("2026-09-01T00:00:00Z"), new Date("2026-09-01T00:00:00Z"));
  }
}
