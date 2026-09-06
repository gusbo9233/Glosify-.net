import process from "node:process";
import path from "node:path";
import { fileURLToPath } from "node:url";

export function storeCallback(extensionId) {
  if (!/^[a-p]{32}$/u.test(extensionId ?? "")) {
    throw new Error("Supply the 32-letter extension ID assigned to your uploaded Store draft (letters a–p).");
  }
  if (extensionId === "gogoghheeaelbddlgiehjdpmnnpnpjfk") {
    throw new Error("This is the local development ID, not the assigned Store ID.");
  }
  return `https://${extensionId}.chromiumapp.org/glosify`;
}

// No login, tokens, text submission, configuration writes, or paid requests.
export async function checkProduction(request = fetch) {
  const failures = [];
  for (const endpoint of ["/api/me", "/api/translator/catalog"]) {
    const response = await request(`https://glosify.se${endpoint}`, {
      redirect: "manual", credentials: "omit", signal: AbortSignal.timeout(20_000),
    });
    await response.arrayBuffer();
    if (response.status !== 401) failures.push(`${endpoint}: expected anonymous HTTP 401; got ${response.status}. Deploy/check the Translator backend before release.`);
  }
  const privacy = await request("https://glosify.se/privacy/english", {
    redirect: "manual", credentials: "omit", signal: AbortSignal.timeout(20_000),
  });
  const policy = await privacy.text();
  if (privacy.status !== 200 || !privacy.headers.get("content-type")?.includes("text/html")
    || !policy.includes("Translator data:") || !policy.includes("Chrome Web Store Limited Use")) {
    failures.push("The public English privacy policy must return HTTP 200 and disclose Translator data and Chrome Limited Use.");
  }
  return failures;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const id = process.argv[2];
  if (id) console.log(`Required production allowlist entry: ${storeCallback(id)}`);
  const failures = await checkProduction();
  for (const failure of failures) console.error(`BLOCKED: ${failure}`);
  if (failures.length) process.exitCode = 1;
  else console.log("Anonymous API and privacy checks passed.");
  console.log("This does NOT verify callback configuration, migrations, account credits, or a real Translate/Save. Complete RELEASE.md before publication.");
  if (!id) console.log("After uploading a draft, rerun with its assigned ID to print the exact callback.");
}
