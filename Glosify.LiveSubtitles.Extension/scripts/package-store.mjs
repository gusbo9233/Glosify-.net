import { mkdir, readdir, rm } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
run(process.execPath, [path.join(root, "scripts/build.mjs"), "store"]);
run(process.execPath, [path.join(root, "scripts/validate-store.mjs"), path.join(root, "artifacts/store")]);

const packageDirectory = path.join(root, "artifacts/package");
const zipPath = path.join(packageDirectory, "glosify-live-subtitles-0.5.0-beta.zip");
await mkdir(packageDirectory, { recursive: true });
await rm(zipPath, { force: true });
const files = await listFiles(path.join(root, "artifacts/store"));
run("zip", ["-X", "-q", zipPath, ...files], path.join(root, "artifacts/store"));
console.log(zipPath);

function run(command, args, cwd = root) {
  const result = spawnSync(command, args, { cwd, encoding: "utf8", env: { ...process.env, TZ: "UTC" } });
  if (result.status !== 0) throw new Error(result.stderr || `${command} failed`);
  if (result.stdout) process.stdout.write(result.stdout);
}

async function listFiles(directory, prefix = "") {
  const result = [];
  for (const entry of (await readdir(directory, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) result.push(...await listFiles(path.join(directory, entry.name), relative));
    else result.push(relative);
  }
  return result;
}
