import test from "node:test";
import assert from "node:assert/strict";
import { appendBounded, normalizeRealtimeEvent } from "../lib/realtime-events.js";

test("translation deltas normalize without retaining provider payloads", () => {
  let sequence = 0;
  const event = normalizeRealtimeEvent(
    { type: "session.output_transcript.delta", delta: "Hola" },
    { sessionId: "session-1", targetLanguage: "es", nextSequence: () => ++sequence });

  assert.equal(event.stream, "translation");
  assert.equal(event.language, "es");
  assert.equal(event.delta, "Hola");
  assert.equal(event.sequence, 1);
  assert.equal(event.isFinal, false);
});

test("Microsoft Foundry translation text deltas normalize", () => {
  const event = normalizeRealtimeEvent(
    { type: "response.text.delta", text: "Hej" },
    { sessionId: "session-1", targetLanguage: "sv", nextSequence: () => 2 });

  assert.equal(event.stream, "translation");
  assert.equal(event.language, "sv");
  assert.equal(event.delta, "Hej");
  assert.equal(event.sequence, 2);
});

test("source transcript events are ignored by the translation-only overlay", () => {
  const event = normalizeRealtimeEvent(
    { type: "session.input_transcript.delta", delta: "Hello", language: "en" },
    { sessionId: "session-1", targetLanguage: "es", nextSequence: () => 4 });

  assert.equal(event, null);
});

test("unrelated realtime events are ignored", () => {
  const event = normalizeRealtimeEvent(
    { type: "session.output_audio.delta", delta: "audio" },
    { sessionId: "session-1", targetLanguage: "es", nextSequence: () => 1 });
  assert.equal(event, null);
});

test("subtitle buffers keep only the configured tail", () => {
  assert.equal(appendBounded("12345", "67890", 6), "567890");
});
