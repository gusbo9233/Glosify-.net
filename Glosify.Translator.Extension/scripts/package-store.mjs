import { mkdir, readFile, rm } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
execFileSync(process.execPath, [path.join(root, "scripts/build.mjs"), "store"], { stdio: "inherit" });
execFileSync(process.execPath, [path.join(root, "scripts/validate-store.mjs"), path.join(root, "artifacts/store")], { stdio: "inherit" });
const manifest = JSON.parse(await readFile(path.join(root, "artifacts/store/manifest.json"), "utf8"));
const destination = path.join(root, "artifacts/package");
await mkdir(destination, { recursive: true });
const archive = path.join(destination, `glosify-translator-${manifest.version}-beta.zip`);
await rm(archive, { force: true });
execFileSync("zip", ["-X", "-q", "-r", archive, "."], { cwd: path.join(root, "artifacts/store") });
console.log(archive);
