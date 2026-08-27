import { isUtf8 } from "node:buffer";
import { readFile, readdir, stat } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const extensionRoot = path.resolve(path.dirname(scriptPath), "..");

export const EXPECTED_STORE_FILES = Object.freeze([
  "background/service-worker.js", "config.js", "content/subtitles.js",
  "icons/icon16.png", "icons/icon32.png", "icons/icon48.png", "icons/icon128.png",
  "lib/audio-pcm.js", "lib/billing.js", "lib/chat-buffer.js", "lib/popup-visibility.js", "lib/realtime-events.js",
  "lib/relay-url.js", "lib/subtitle-appearance.js", "lib/transcript-storage.js", "manifest.json",
  "offscreen/offscreen.html", "offscreen/offscreen.js",
  "popup/popup.css", "popup/popup.html", "popup/popup.js",
]);

export async function validateStore(directory) {
  const root = path.resolve(directory);
  const failures = [];
  let manifest = null;
  let files = [];

  try {
    manifest = JSON.parse(await readFile(path.join(root, "manifest.json"), "utf8"));
  } catch (error) {
    failures.push(`invalid Store manifest: ${error.message}`);
  }

  if (manifest) {
    if (manifest.version !== "0.5.0") failures.push("manifest version must be 0.5.0");
    if ("key" in manifest) failures.push("Store manifest must not contain a pinned development key");
    if (JSON.stringify(manifest.host_permissions) !== JSON.stringify(["https://glosify.se/*"])) failures.push("Store host permissions must contain only https://glosify.se/*");
  }

  try {
    files = await listFiles(root);
  } catch (error) {
    failures.push(`could not enumerate Store files: ${error.message}`);
  }

  const expected = new Set(EXPECTED_STORE_FILES);
  for (const file of files) if (!expected.has(file)) failures.push(`unexpected packaged file: ${file}`);
  for (const file of expected) if (!files.includes(file)) failures.push(`missing packaged file: ${file}`);

  for (const file of files) {
    const bytes = await readFile(path.join(root, file));
    const text = textContents(bytes);
    if (text === null) {
      continue;
    }
    if (/localhost|127\.0\.0\.1/iu.test(text)) failures.push(`localhost Store configuration in ${file}`);
    if (/\beval\s*\(|new\s+Function\s*\(|importScripts\s*\(\s*["']https?:|<script\b[^>]*\bsrc\s*=\s*["']https?:/iu.test(text)) failures.push(`remote or dynamic code in ${file}`);
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

  return { failures, files };
}

export async function assertValidStore(directory) {
  const result = await validateStore(directory);
  if (result.failures.length) {
    throw new Error(`Store validation failed:\n- ${result.failures.join("\n- ")}`);
  }
  return result;
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

function textContents(bytes) {
  return !bytes.includes(0) && isUtf8(bytes) ? bytes.toString("utf8") : null;
}

async function pngDimensions(file) {
  if (!(await stat(file)).isFile()) throw new Error(`missing icon: ${path.basename(file)}`);
  const bytes = await readFile(file);
  if (bytes.length < 24 || bytes.toString("hex", 0, 8) !== "89504e470d0a1a0a") throw new Error(`${path.basename(file)} is not a PNG`);
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  const root = path.resolve(process.argv[2] ?? path.join(extensionRoot, "artifacts/store"));
  const result = await assertValidStore(root);
  console.log(`Validated ${result.files.length} Store files in ${root} for Chrome Web Store upload`);
}
