import { readFile, readdir, stat } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { createHash } from "node:crypto";

const root = path.resolve(process.argv[2] ?? "artifacts/store");
const manifest = JSON.parse(await readFile(path.join(root, "manifest.json"), "utf8"));
const failures = [];

if (manifest.version !== "0.5.0") failures.push("manifest version must be 0.5.0");
if (manifest.key !== "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA3EWb0lg1qHn+jCKdT5RdBP7WQ5TzaEvs3XC4WoXwijmdGApbCjgsHV7PwcCn/Qwemj3x2YmBoUO1efXXHnTawC1gjfgmsCz41aRaIiBnBhNbRtjMalB0cZ59qjEm6Ivyzy+n8NZMDpTY25GBDcb4WFMG9HC8BFBlyc+g5bfvwZYHrnh75HKQ343v5JmR/37m9M74zxYrXYzO4xzNkojWUntuEenNd7/KQz004+FwFIC+GenXlqQd3KwmTHeUChoSMC0Xw1FlDd6bKETE8EZy+ZzFxHDTvv8AUsWWeCyoOo9XNI4VjkvLSiZTfuKc3D0GiJn5Nkca4nnwKFdAHC3X3QIDAQAB") failures.push("pinned extension key changed");
const extensionId = extensionIdForKey(manifest.key);
if (extensionId !== "akepdpjieiokffdapibipomhbplikock") failures.push(`pinned extension ID changed to ${extensionId}`);
if (JSON.stringify(manifest.host_permissions) !== JSON.stringify(["https://glosify.se/*"])) failures.push("Store host permissions must contain only https://glosify.se/*");

const expected = new Set([
  "background/service-worker.js", "config.js", "content/subtitles.js",
  "icons/icon16.png", "icons/icon32.png", "icons/icon48.png", "icons/icon128.png",
  "lib/audio-pcm.js", "lib/billing.js", "lib/chat-buffer.js", "lib/popup-visibility.js", "lib/realtime-events.js",
  "lib/relay-url.js", "lib/transcript-storage.js", "manifest.json",
  "offscreen/offscreen.html", "offscreen/offscreen.js",
  "popup/popup.css", "popup/popup.html", "popup/popup.js",
]);
const files = await listFiles(root);
for (const file of files) if (!expected.has(file)) failures.push(`unexpected packaged file: ${file}`);
for (const file of expected) if (!files.includes(file)) failures.push(`missing packaged file: ${file}`);

for (const file of files.filter(file => /\.(?:js|json|html|css)$/u.test(file))) {
  const text = await readFile(path.join(root, file), "utf8");
  if ((file === "manifest.json" || file === "config.js")
      && /localhost|127\.0\.0\.1/iu.test(text)) failures.push(`localhost Store configuration in ${file}`);
  if (/\beval\s*\(|new\s+Function\s*\(|importScripts\s*\(\s*["']https?:|<script[^>]+src=["']https?:/iu.test(text)) failures.push(`remote or dynamic code in ${file}`);
  if (/(?:api[_-]?key|client[_-]?secret|password)\s*[:=]\s*["'][^"'${}]{8,}["']/iu.test(text)) failures.push(`possible embedded secret in ${file}`);
}

for (const size of [16, 32, 48, 128]) {
  const icon = path.join(root, `icons/icon${size}.png`);
  try {
    const dimensions = await pngDimensions(icon);
    if (dimensions.width !== size || dimensions.height !== size) failures.push(`icon${size}.png must be ${size}x${size}`);
  } catch (error) {
    failures.push(error.message);
  }
}

if (failures.length) throw new Error(`Store validation failed:\n- ${failures.join("\n- ")}`);
console.log(`Validated ${files.length} Store files in ${root} for extension ${extensionId}`);

async function listFiles(directory, prefix = "") {
  const result = [];
  for (const entry of (await readdir(directory, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) result.push(...await listFiles(path.join(directory, entry.name), relative));
    else result.push(relative);
  }
  return result;
}

async function pngDimensions(file) {
  if (!(await stat(file)).isFile()) throw new Error(`missing icon: ${path.basename(file)}`);
  const bytes = await readFile(file);
  if (bytes.length < 24 || bytes.toString("hex", 0, 8) !== "89504e470d0a1a0a") throw new Error(`${path.basename(file)} is not a PNG`);
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

function extensionIdForKey(key) {
  const hex = createHash("sha256")
    .update(Buffer.from(key, "base64"))
    .digest("hex")
    .slice(0, 32);
  return [...hex]
    .map(character => String.fromCharCode(97 + Number.parseInt(character, 16)))
    .join("");
}
