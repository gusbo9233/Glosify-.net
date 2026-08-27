import test from "node:test";
import assert from "node:assert/strict";
import { buildRelayProtocols, buildRelayWebSocketUrl } from "../lib/relay-url.js";

const sessionId = "36ec3a0a-b0fb-4aa5-a216-46a78e9d7356";
const path = `/api/realtime-translation/sessions/${sessionId}/stream`;
const token = "A".repeat(43);

test("builds a secure same-origin Glosify relay URL", () => {
  assert.equal(
    buildRelayWebSocketUrl("https://glosify.example", path, sessionId),
    `wss://glosify.example${path}`);
  assert.equal(
    buildRelayWebSocketUrl(
      "http://localhost:5000",
      path,
      sessionId,
      { allowInsecure: true }),
    `ws://localhost:5000${path}`);
});

test("rejects insecure relay origins unless the build explicitly permits them", () => {
  assert.throws(
    () => buildRelayWebSocketUrl("http://localhost:5000", path, sessionId),
    /invalid subtitle relay URL/);
});

test("rejects cross-origin, query, and mismatched session relay paths", () => {
  assert.throws(
    () => buildRelayWebSocketUrl("https://glosify.example", `https://evil.example${path}`, sessionId),
    /invalid subtitle relay URL/);
  assert.throws(
    () => buildRelayWebSocketUrl("https://glosify.example", `${path}?token=leak`, sessionId),
    /invalid subtitle relay URL/);
  assert.throws(
    () => buildRelayWebSocketUrl(
      "https://glosify.example",
      "/api/realtime-translation/sessions/8c86023d-598f-4dd2-9367-45c4ea65187e/stream",
      sessionId),
    /invalid subtitle relay URL/);
});

test("puts only a bounded short-lived relay grant in a WebSocket subprotocol", () => {
  assert.deepEqual(buildRelayProtocols(token), [
    "glosify-realtime",
    `relay-token.${token}`,
  ]);
  assert.throws(() => buildRelayProtocols("not-a-token"), /invalid subtitle relay token/);
});
