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

test("settings remain persistable while the service-worker catalog is absent", () => {
  const settings = State.normalizeSettingsForStorage({
    sourceLanguage: "sr-Latn",
    targetLanguage: "zh-Hans",
    preferences: "Informal",
  }, null);

  assert.deepEqual(settings, {
    sourceLanguage: "sr-Latn",
    targetLanguage: "zh-Hans",
    preferences: "Informal",
  });
  assert.deepEqual(State.normalizeSettingsForStorage({
    sourceLanguage: "unsupported-language",
    targetLanguage: "",
    preferences: "x".repeat(501),
  }, null), {
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

test("request ids use getRandomValues without requiring a secure context", () => {
  const insecureContextCrypto = {
    getRandomValues(bytes) {
      bytes.fill(0);
      return bytes;
    },
  };

  assert.equal(
    State.createRequestId(insecureContextCrypto),
    "00000000-0000-4000-8000-000000000000");
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
    saveLanguage: null,
    saved: false,
  });
});

test("Problem Details compatibility error is parsed", () => {
  assert.equal(State.parseProblem({ error: "No credits" }, 402).message, "No credits");
});
