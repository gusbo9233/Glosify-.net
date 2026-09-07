import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const worker = readFileSync(new URL("../background/service-worker.js", import.meta.url), "utf8");
const authStart = worker.indexOf("async function acceptTokenResponse(");
const authEnd = worker.indexOf("async function refreshAccountState(");
const errorStart = worker.indexOf("class ApiRequestError extends Error");
assert.ok(authStart > 0 && authEnd > authStart && errorStart > authEnd);

function harness(fetch) {
  const storage = { glosifyRefreshToken: "original-refresh" };
  const auth = vm.runInNewContext(`
    const CONFIG = { glosifyBaseUrl: "https://example.invalid" };
    const STORAGE_KEYS = { refreshToken: "glosifyRefreshToken" };
    let accessToken = null, accessExpiresAt = 0, refreshToken = "original-refresh";
    let refreshTokenGeneration = 1, refreshPromise = null;
    const state = { signedIn: true, status: "ready", catalog: {} };
    ${worker.slice(authStart, authEnd)}
    ${worker.slice(errorStart)}
    ({ ensureAccessToken, acceptTokenResponse, state });
  `, {
    fetch, URL, Headers, Date,
    chrome: { storage: { local: {
      set: async values => Object.assign(storage, values),
      remove: async key => { delete storage[key]; },
    } } },
  });
  return { ...auth, storage };
}
const tokens = { accessToken: "new-access", refreshToken: "new-refresh", expiresIn: 3600 };

for (const status of [429, 503]) {
  test(`refresh ${status} preserves credentials and error details, then retries successfully`, async () => {
    let requests = 0;
    const auth = harness(async (_url, options) => {
      requests += 1;
      assert.equal(JSON.parse(options.body).refreshToken, "original-refresh");
      return requests === 1
        ? Response.json({ detail: "Try later", code: "temporary", resetsAtUtc: "2026-09-07T00:00:00Z" }, { status })
        : Response.json(tokens);
    });
    await assert.rejects(auth.ensureAccessToken(), {
      status, message: "Try later", code: "temporary", resetsAtUtc: "2026-09-07T00:00:00Z",
    });
    assert.equal(auth.storage.glosifyRefreshToken, "original-refresh");
    assert.equal(auth.state.signedIn, true);
    assert.equal(await auth.ensureAccessToken(), "new-access");
    assert.equal(auth.storage.glosifyRefreshToken, "new-refresh");
    assert.equal(requests, 2);
  });
}

test("non-JSON server errors retain status without exposing the upstream body", async () => {
  const auth = harness(async () => new Response("private upstream diagnostics", { status: 502 }));
  await assert.rejects(auth.ensureAccessToken(), { status: 502, message: "Glosify request failed (502)." });
  assert.equal(auth.storage.glosifyRefreshToken, "original-refresh");
  assert.equal(auth.state.signedIn, true);
});

test("network failures retain credentials and allow the next refresh", async () => {
  let attempts = 0;
  const auth = harness(async () => {
    if (++attempts === 1) throw new TypeError("Network unavailable");
    return Response.json(tokens);
  });
  await assert.rejects(auth.ensureAccessToken(), { message: "Network unavailable" });
  assert.equal(auth.storage.glosifyRefreshToken, "original-refresh");
  assert.equal(auth.state.signedIn, true);
  assert.equal(await auth.ensureAccessToken(), "new-access");
});

for (const status of [401, 403]) {
  test(`refresh ${status} clears rejected credentials and disconnects`, async () => {
    const auth = harness(async () => new Response(null, { status }));
    await assert.rejects(auth.ensureAccessToken(), { status: 401, message: "Your Glosify session expired. Connect again." });
    assert.equal(auth.storage.glosifyRefreshToken, undefined);
    assert.equal(auth.state.signedIn, false);
    assert.equal(auth.state.status, "disconnected");
    assert.equal(auth.state.catalog, null);
  });

  test(`stale refresh ${status} cannot clear a newer login`, async () => {
    let respond;
    const auth = harness(() => new Promise(resolve => { respond = resolve; }));
    const pending = auth.ensureAccessToken();
    await auth.acceptTokenResponse(tokens);
    respond(new Response(null, { status }));
    await assert.rejects(pending, { status: 401 });
    assert.equal(auth.storage.glosifyRefreshToken, "new-refresh");
    assert.equal(auth.state.signedIn, true);
    assert.equal(await auth.ensureAccessToken(), "new-access");
  });
}

test("concurrent callers share one failed refresh and can share a successful retry", async () => {
  let respond;
  let attempts = 0;
  const auth = harness(() => {
    attempts += 1;
    return new Promise(resolve => { respond = resolve; });
  });
  const failed = Promise.allSettled([auth.ensureAccessToken(), auth.ensureAccessToken()]);
  assert.equal(attempts, 1);
  respond(new Response(null, { status: 503 }));
  for (const result of await failed) {
    assert.equal(result.status, "rejected");
    assert.equal(result.reason.status, 503);
  }
  const retry = Promise.all([auth.ensureAccessToken(), auth.ensureAccessToken()]);
  assert.equal(attempts, 2);
  respond(Response.json(tokens));
  assert.deepEqual(await retry, ["new-access", "new-access"]);
});
