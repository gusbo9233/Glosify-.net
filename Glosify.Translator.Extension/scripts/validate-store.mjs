import { access, readFile, readdir } from "node:fs/promises";
import path from "node:path";
import process from "node:process";

const directory = path.resolve(process.argv[2] ?? "artifacts/store");
const manifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
const expectedPermissions = ["activeTab", "clipboardWrite", "identity", "scripting", "storage"];
if (manifest.manifest_version !== 3) throw new Error("Store build must use Manifest V3.");
if (JSON.stringify([...manifest.permissions].sort()) !== JSON.stringify(expectedPermissions)) {
  throw new Error(`Unexpected permissions: ${manifest.permissions.join(", ")}`);
}
if (JSON.stringify(manifest.host_permissions) !== JSON.stringify(["https://glosify.se/*"])) {
  throw new Error("Store build may access only https://glosify.se/*.");
}
if ("key" in manifest) throw new Error("The Store manifest must not contain a development key.");
for (const file of [manifest.background.service_worker, manifest.action.default_popup, ...Object.values(manifest.icons)]) {
  await access(path.join(directory, file));
}
const allFiles = await listFiles(directory);
if (allFiles.some(file => file.includes("test") || file.endsWith(".map"))) {
  throw new Error("Store build contains test or source-map files.");
}
console.log(`Validated ${directory}`);

async function listFiles(target, prefix = "") {
  const result = [];
  for (const entry of await readdir(target, { withFileTypes: true })) {
    const relative = path.join(prefix, entry.name);
    if (entry.isDirectory()) result.push(...await listFiles(path.join(target, entry.name), relative));
    else result.push(relative);
  }
  return result;
}
