import assert from "node:assert/strict";
import test from "node:test";
import "../lib/translator-state.js";

const State = globalThis.GlosifyTranslatorState;
const catalog = {
  languages: [{ code: "en", name: "English" }, { code: "es", name: "Spanish" }],
  sourceLanguages: [{ code: "auto", name: "Auto-detect" }, { code: "en", name: "English" }, { code: "es", name: "Spanish" }],
  maxPreferenceCharacters: 500,
};

test("settings restore only supported languages and bound preferences", () => {
  assert.deepEqual(State.normalizeSettings({ sourceLanguage: "xx", targetLanguage: "xx", preferences: "x".repeat(501) }, catalog), {
    sourceLanguage: "auto",
    targetLanguage: "en",
    preferences: "x".repeat(500),
  });
});

test("editing invalidates an existing save snapshot", () => {
  const result = State.invalidate({ result: { translatedText: "Hola" }, requestId: "id", saved: true });
  assert.equal(result.result, null);
  assert.equal(result.requestId, null);
  assert.equal(result.saved, false);
});

test("auto source swaps only after a detected source is available", () => {
  const original = { sourceLanguage: "auto", targetLanguage: "en", sourceText: "Hola", result: null };
  assert.equal(State.swap(original), original);
  assert.deepEqual(State.swap({ ...original, result: { detectedSourceLanguage: "es", translatedText: "Hello" } }), {
    ...original,
    sourceLanguage: "en",
    targetLanguage: "es",
    sourceText: "Hello",
    result: null,
    requestId: null,
    saved: false,
  });
});

test("Problem Details compatibility error is parsed", () => {
  assert.equal(State.parseProblem({ error: "No credits" }, 402).message, "No credits");
});
