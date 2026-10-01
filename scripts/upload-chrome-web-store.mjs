import { readFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";
import { setTimeout as delay } from "node:timers/promises";

// Upload only: publication remains an explicit action in the Store dashboard.
export async function uploadDraft(archive, env, { request = fetch, wait = delay, statusOnly = false } = {}) {
  for (const key of ["CWS_PUBLISHER_ID", "CWS_EXTENSION_ID", "CWS_CLIENT_ID", "CWS_CLIENT_SECRET", "CWS_REFRESH_TOKEN"]) {
    if (!env[key]?.trim()) throw new Error(`Missing ${key}`);
  }
  if (!/^[a-p]{32}$/.test(env.CWS_EXTENSION_ID)) throw new Error("Invalid CWS_EXTENSION_ID");
  if (!/^[a-zA-Z0-9_-]+$/.test(env.CWS_PUBLISHER_ID)) throw new Error("Invalid CWS_PUBLISHER_ID");

  let accessToken = "";
  async function json(url, options, label) {
    const response = await request(url, { ...options, redirect: "error", signal: AbortSignal.timeout(120_000) });
    if (!response.ok) {
      let detail = "check credentials and the Store dashboard";
      // Only expose the Store's validation message, never OAuth response bodies.
      if (label.startsWith("Store ")) {
        const body = await response.json().catch(() => null);
        if (typeof body?.error?.message === "string") {
          detail = body.error.message;
          for (const credential of [env.CWS_CLIENT_ID, env.CWS_CLIENT_SECRET, env.CWS_REFRESH_TOKEN, accessToken]
            .filter(Boolean).sort((a, b) => b.length - a.length)) {
            if (credential) detail = detail.replaceAll(credential, "[redacted]");
          }
          detail = detail.replace(/[\r\n]+/g, " ").slice(0, 2000);
        }
      }
      throw new Error(`${label} failed (HTTP ${response.status}); ${detail}`);
    }
    return response.json();
  }

  const token = await json("https://oauth2.googleapis.com/token", {
    method: "POST",
    body: new URLSearchParams({
      client_id: env.CWS_CLIENT_ID,
      client_secret: env.CWS_CLIENT_SECRET,
      refresh_token: env.CWS_REFRESH_TOKEN,
      grant_type: "refresh_token",
    }),
  }, "OAuth token exchange");
  if (typeof token.access_token !== "string" || !token.access_token) throw new Error("OAuth response has no access token");
  accessToken = token.access_token;

  const name = `publishers/${env.CWS_PUBLISHER_ID}/items/${env.CWS_EXTENSION_ID}`;
  const headers = { Authorization: `Bearer ${token.access_token}` };
  if (statusOnly) {
    const status = await json(`https://chromewebstore.googleapis.com/v2/${name}:fetchStatus`, {
      headers,
    }, "Store status");
    return {
      status,
      refreshTokenExpiresIn: Number.isFinite(token.refresh_token_expires_in)
        ? token.refresh_token_expires_in : null,
    };
  }
  const uploaded = await json(`https://chromewebstore.googleapis.com/upload/v2/${name}:upload`, {
    method: "POST",
    headers: { ...headers, "Content-Type": "application/zip" },
    body: archive,
  }, "Store upload");
  let state = uploaded.uploadState;
  for (let attempt = 0; attempt < 30; attempt++) {
    if (state === "SUCCEEDED") return;
    // The guide also calls this UPLOAD_IN_PROGRESS; accept both spellings.
    if (state !== "IN_PROGRESS" && state !== "UPLOAD_IN_PROGRESS") {
      throw new Error("Store upload did not succeed; inspect the Store dashboard");
    }
    await wait(10_000);
    const status = await json(`https://chromewebstore.googleapis.com/v2/${name}:fetchStatus`, {
      headers,
    }, "Store upload status");
    state = status.lastAsyncUploadState;
  }
  if (state !== "SUCCEEDED") throw new Error("Store upload is still processing; check the dashboard before retrying");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    if (process.argv[2] === "--status") {
      const result = await uploadDraft(null, process.env, { statusOnly: true });
      console.log("Chrome Web Store authentication and item access verified.");
      console.log(`Submitted revision state: ${result.status.submittedItemRevisionStatus?.state ?? "none"}`);
      console.log(`Last upload state: ${result.status.lastAsyncUploadState ?? "unknown"}`);
      if (result.refreshTokenExpiresIn !== null) {
        console.log(`Refresh token expires in ${Math.floor(result.refreshTokenExpiresIn / 86400)} days. Move the OAuth app out of Testing and replace this temporary token for ongoing automation.`);
      }
    } else {
      if (!process.argv[2]) throw new Error("Usage: node scripts/upload-chrome-web-store.mjs <extension.zip|--status>");
      await uploadDraft(await readFile(process.argv[2]), process.env);
      console.log("Chrome Web Store draft uploaded successfully. Publication is manual.");
    }
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
