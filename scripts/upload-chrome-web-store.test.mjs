import { test } from "node:test";
import assert from "node:assert/strict";
import { uploadDraft } from "./upload-chrome-web-store.mjs";

const env = {
  CWS_PUBLISHER_ID: "publisher-123",
  CWS_EXTENSION_ID: "a".repeat(32),
  CWS_CLIENT_ID: "client",
  CWS_CLIENT_SECRET: "secret",
  CWS_REFRESH_TOKEN: "refresh",
};

function mock(responses) {
  const calls = [];
  return {
    calls,
    request: async (url, options) => {
      calls.push({ url, options });
      assert.ok(responses.length, "Unexpected additional HTTP request");
      const body = responses.shift();
      return { ok: true, json: async () => body };
    },
    wait: async () => {},
  };
}

test("uploads the exact ZIP using OAuth and never calls publish", async () => {
  const archive = Buffer.from("zip-content");
  const api = mock([{ access_token: "access" }, { uploadState: "SUCCEEDED" }]);
  await uploadDraft(archive, env, api);
  assert.equal(api.calls.length, 2);
  assert.equal(api.calls[0].options.body.get("refresh_token"), "refresh");
  assert.equal(api.calls[1].url, `https://chromewebstore.googleapis.com/upload/v2/publishers/publisher-123/items/${env.CWS_EXTENSION_ID}:upload`);
  assert.equal(api.calls[1].options.method, "POST");
  assert.equal(api.calls[1].options.body, archive);
  assert.equal(api.calls[1].options.headers.Authorization, "Bearer access");
  assert.equal(api.calls[1].options.redirect, "error");
});

test("waits for asynchronous upload completion", async () => {
  const api = mock([{ access_token: "access" }, { uploadState: "IN_PROGRESS" },
    { lastAsyncUploadState: "IN_PROGRESS" }, { lastAsyncUploadState: "SUCCEEDED" }]);
  await uploadDraft(Buffer.alloc(0), env, api);
  assert.equal(api.calls.length, 4);
  assert.ok(api.calls[2].url.endsWith(":fetchStatus"));
});

test("rejects failed, missing, and unknown upload states", async () => {
  for (const state of ["FAILED", "NOT_FOUND", undefined, "unexpected"]) {
    const api = mock([{ access_token: "access" }, { uploadState: "IN_PROGRESS" }, { lastAsyncUploadState: state }]);
    await assert.rejects(uploadDraft(Buffer.alloc(0), env, api), /did not succeed/);
  }
});

test("stops polling when the upload never completes", async () => {
  const api = mock([{ access_token: "access" }, { uploadState: "UPLOAD_IN_PROGRESS" },
    ...Array.from({ length: 30 }, () => ({ lastAsyncUploadState: "IN_PROGRESS" }))]);
  await assert.rejects(uploadDraft(Buffer.alloc(0), env, api), /still processing/);
  assert.equal(api.calls.length, 32);
});

test("rejects missing configuration before making any request", async () => {
  for (const key of Object.keys(env)) {
    const api = mock([]);
    await assert.rejects(uploadDraft(Buffer.alloc(0), { ...env, [key]: "" }, api), /Missing/);
    assert.equal(api.calls.length, 0);
  }
});

test("HTTP errors do not expose response bodies or credentials", async () => {
  await assert.rejects(uploadDraft(Buffer.alloc(0), env, {
    request: async () => ({ ok: false, status: 401, json: async () => { throw new Error("secret"); } }),
  }), error => error.message.includes("HTTP 401") && !error.message.includes("secret"));
});

test("Store validation errors retain the actionable reason and redact credentials", async () => {
  let calls = 0;
  await assert.rejects(uploadDraft(Buffer.alloc(0), env, {
    request: async () => ++calls === 1
      ? { ok: true, json: async () => ({ access_token: "access-token-value" }) }
      : { ok: false, status: 400, json: async () => ({ error: {
        message: "Version must increase.\nclient secret refresh access-token-value",
      } }) },
  }), error => {
    assert.equal(error.message, "Store upload failed (HTTP 400); Version must increase. [redacted] [redacted] [redacted] [redacted]");
    return true;
  });
});

test("non-JSON Store failures retain the HTTP status", async () => {
  let calls = 0;
  await assert.rejects(uploadDraft(Buffer.alloc(0), env, {
    request: async () => ++calls === 1
      ? { ok: true, json: async () => ({ access_token: "access" }) }
      : { ok: false, status: 502, json: async () => { throw new Error("invalid JSON"); } },
  }), /Store upload failed \(HTTP 502\)/);
});
