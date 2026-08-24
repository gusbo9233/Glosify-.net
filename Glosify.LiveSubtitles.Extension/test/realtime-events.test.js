import test from "node:test";
import assert from "node:assert/strict";
import {
  appendBounded,
  createRealtimeEventAccumulator,
  normalizeRealtimeEvent,
} from "../lib/realtime-events.js";

test("Scribe finalized segments normalize into committed translation events", () => {
  const event = normalizeRealtimeEvent({
    type: "glosify.translation.segment",
    sequence: 7,
    sourceLanguage: "pl",
    targetLanguage: "sv",
    text: "God morgon",
  }, { sessionId: "s1", targetLanguage: "sv", nextSequence: () => 99 });

  const { clientTimestamp, ...stableFields } = event;
  assert.equal(typeof clientTimestamp, "number");
  assert.ok(Number.isFinite(clientTimestamp));
  assert.deepEqual(stableFields, {
    sessionId: "s1",
    stream: "translation",
    language: "sv",
    sourceLanguage: "pl",
    sequence: 7,
    delta: "God morgon",
    replace: true,
    isFinal: true,
  });
});

test("Scribe partial translations replace the mutable live line", () => {
  const event = normalizeRealtimeEvent({
    type: "glosify.translation.partial",
    sequence: 3,
    sourceLanguage: "de",
    targetLanguage: "en",
    text: "Good morning",
  }, { sessionId: "s1", targetLanguage: "en", nextSequence: () => 99 });

  assert.equal(event.delta, "Good morning");
  assert.equal(event.replace, true);
  assert.equal(event.isFinal, false);
  assert.equal(event.sequence, 3);
});

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

test("OpenAI translation text deltas normalize", () => {
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

test("OpenAI deltas replace one stable caption and the final prefers complete text", () => {
  let sequence = 0;
  const accumulator = createRealtimeEventAccumulator({
    sessionId: "s1",
    targetLanguage: "sv",
    nextSequence: () => ++sequence,
  });
  const first = accumulator.apply({
    type: "response.output_text.delta",
    response_id: "response-1",
    output_index: 0,
    content_index: 0,
    delta: "God ",
  }, 1_000);
  const second = accumulator.apply({
    type: "response.output_text.delta",
    response_id: "response-1",
    output_index: 0,
    content_index: 0,
    delta: "morgon",
  }, 1_100);
  const final = accumulator.apply({
    type: "response.output_text.done",
    response_id: "response-1",
    output_index: 0,
    content_index: 0,
    text: "God morgon!",
  }, 1_200);

  assert.equal(first.sequence, second.sequence);
  assert.equal(second.delta, "God morgon");
  assert.equal(second.replace, true);
  assert.equal(final.sequence, first.sequence);
  assert.equal(final.delta, "God morgon!");
  assert.equal(final.isFinal, true);
});

test("final-only provider events render once", () => {
  let sequence = 0;
  const accumulator = createRealtimeEventAccumulator({
    sessionId: "s1",
    targetLanguage: "de",
    nextSequence: () => ++sequence,
  });
  const providerEvent = {
    type: "response.text.done",
    item_id: "item-7",
    content_index: 1,
    transcript: "Guten Abend",
  };

  assert.equal(accumulator.apply(providerEvent, 2_000).delta, "Guten Abend");
  assert.equal(accumulator.apply(providerEvent, 2_100), null);
});

test("delta-only captions finalize after four idle seconds", () => {
  const accumulator = createRealtimeEventAccumulator({
    sessionId: "s1",
    targetLanguage: "pl",
    nextSequence: () => 4,
  });
  accumulator.apply({
    type: "response.text.delta",
    response_id: "response-idle",
    delta: "Dobry wieczór",
  }, 1_000);

  assert.deepEqual(accumulator.flushIdle(4_999), []);
  const [final] = accumulator.flushIdle(5_000);
  assert.equal(final.delta, "Dobry wieczór");
  assert.equal(final.sequence, 4);
  assert.equal(final.isFinal, true);
});

test("completed provider IDs retain only a bounded deduplication window", () => {
  const accumulator = createRealtimeEventAccumulator({
    sessionId: "s1",
    targetLanguage: "en",
    nextSequence: () => 1,
  }, { maximumCompletedKeys: 2 });
  const final = responseId => ({
    type: "response.text.done",
    response_id: responseId,
    text: responseId,
  });

  assert.equal(accumulator.apply(final("one"), 1).delta, "one");
  assert.equal(accumulator.apply(final("two"), 2).delta, "two");
  assert.equal(accumulator.apply(final("three"), 3).delta, "three");
  assert.equal(accumulator.apply(final("one"), 4).delta, "one");
  assert.equal(accumulator.apply(final("three"), 5), null);
});
