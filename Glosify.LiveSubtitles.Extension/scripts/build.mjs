import { cp, mkdir, readFile, rm, utimes, writeFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const profile = process.argv[2];
if (!new Set(["development", "store", "test", "test-tab"]).has(profile)) {
  throw new Error("Build profile must be 'development', 'store', 'test', or 'test-tab'.");
}

const output = path.join(root, "artifacts", profile);
const directories = ["background", "content", "lib", "offscreen", "popup", "icons"];
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
for (const directory of directories) {
  await cp(path.join(root, directory), path.join(output, directory), { recursive: true });
}
await cp(path.join(root, `config.${profile}.js`), path.join(output, "config.js"));

const base = JSON.parse(await readFile(path.join(root, "manifest.base.json"), "utf8"));
const overlay = JSON.parse(await readFile(path.join(root, `manifest.${profile}.json`), "utf8"));
const manifest = { ...base, ...overlay };
await writeFile(path.join(output, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);

const fixedTime = new Date("2026-08-10T00:00:00.000Z");
await normalizeTimes(output);
console.log(output);

async function normalizeTimes(directory) {
  const { readdir } = await import("node:fs/promises");
  const entries = await readdir(directory, { withFileTypes: true });
  for (const entry of entries) {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      await normalizeTimes(target);
    }
    await utimes(target, fixedTime, fixedTime);
  }
}
