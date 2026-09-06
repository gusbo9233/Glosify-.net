import { access, readFile, readdir } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

export async function validateStore(directory) {
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
  const allowedManifestKeys = ["manifest_version", "name", "description", "version", "minimum_chrome_version",
    "homepage_url", "permissions", "web_accessible_resources", "background", "action", "icons",
    "host_permissions", "content_security_policy"];
  if (Object.keys(manifest).some(key => !allowedManifestKeys.includes(key))) {
    throw new Error("Store manifest contains an unreviewed capability or field.");
  }
  const policy = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; connect-src https://glosify.se";
  if (manifest.content_security_policy?.extension_pages !== policy
    || Object.keys(manifest.content_security_policy).length !== 1) {
    throw new Error("Store CSP must allow only packaged code and the production API.");
  }
  const config = await readFile(path.join(directory, "config.js"), "utf8");
  if (config.replace(/\s/gu, "") !== 'exportconstCONFIG=Object.freeze({glosifyBaseUrl:"https://glosify.se",testHooksEnabled:false,});') {
    throw new Error("Store config must target production with test hooks disabled.");
  }
  if (JSON.stringify(manifest.web_accessible_resources) !== JSON.stringify([{
    resources: ["overlay/translator.html"], matches: ["http://*/*", "https://*/*"],
  }])) {
    throw new Error("Only the translator frame HTML may be web-accessible.");
  }
  for (const file of ["overlay/translator.html", "overlay/translator.js", "overlay/translator.css"]) {
    await access(path.join(directory, file));
  }
  for (const file of [manifest.background.service_worker, manifest.action.default_popup, ...Object.values(manifest.icons)]) {
    await access(path.join(directory, file));
  }
  const allFiles = await listFiles(directory);
  const expectedFiles = ["manifest.json", "config.js", "background/service-worker.js", "content/translator.js",
    "lib/translator-state.js", "overlay/translator.html", "overlay/translator.js", "overlay/translator.css",
    "popup/popup.html", "popup/popup.js", "popup/popup.css", "icons/source.svg",
    "icons/icon16.png", "icons/icon32.png", "icons/icon48.png", "icons/icon128.png"];
  if (JSON.stringify(allFiles.sort()) !== JSON.stringify(expectedFiles.sort())) {
    throw new Error("Store build contains missing or unreviewed files.");
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const directory = path.resolve(process.argv[2] ?? "artifacts/store");
  await validateStore(directory);
  console.log(`Validated ${directory}`);
}

async function listFiles(target, prefix = "") {
  const result = [];
  for (const entry of await readdir(target, { withFileTypes: true })) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) result.push(...await listFiles(path.join(target, entry.name), relative));
    else result.push(relative);
  }
  return result;
}
