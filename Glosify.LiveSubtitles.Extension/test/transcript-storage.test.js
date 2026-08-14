import test from "node:test";
import assert from "node:assert/strict";
import {
  buildTranscriptSessionRequest,
  canSaveSourceTranscript,
  clearTranscriptStorageState,
  getEffectiveCreditsPerMinute,
  isTranscriptToggleDisabled,
  selectAvailableTranslationMode,
} from "../lib/transcript-storage.js";

test("transcript storage is opt-in and does not send an id while disabled", () => {
  assert.deepEqual(buildTranscriptSessionRequest({
    targetLanguage: "es",
    saveTranscript: false,
    transcriptId: "ignored",
  }), {
    targetLanguage: "es",
    saveTranscript: false,
  });
});

test("reconnect preserves the opted-in transcript id", () => {
  assert.deepEqual(buildTranscriptSessionRequest({
    targetLanguage: "sv",
    saveTranscript: true,
    transcriptId: "2145953e-a101-40fb-9859-6bd80225695e",
  }), {
    targetLanguage: "sv",
    saveTranscript: true,
    sourceLanguage: "auto",
    transcriptId: "2145953e-a101-40fb-9859-6bd80225695e",
  });
});

test("speech recognition modes include mode and source language", () => {
  assert.deepEqual(buildTranscriptSessionRequest({
    targetLanguage: "sv",
    translationMode: "scribe",
    sourceLanguage: "pl",
    saveTranscript: false,
  }), {
    targetLanguage: "sv",
    translationMode: "scribe",
    sourceLanguage: "pl",
    saveTranscript: false,
  });
});

test("stopping clears consent and transcript identity", () => {
  const state = clearTranscriptStorageState({ saveTranscript: true, transcriptId: "saved" });
  assert.equal(state.saveTranscript, false);
  assert.equal(state.transcriptId, null);
});

test("source saving requires only feature enablement and an assigned quiz language", () => {
  const catalog = {
    savedSourceTranscriptsEnabled: true,
    selectedQuizLanguage: { code: "pl", name: "Polish" },
  };
  assert.equal(canSaveSourceTranscript(catalog), true);
  assert.equal(canSaveSourceTranscript({ ...catalog, savedSourceTranscriptsEnabled: false }), false);
  assert.equal(canSaveSourceTranscript({ ...catalog, selectedQuizLanguage: null }), false);
});

test("source saving switches the displayed and preflight rate to sixteen credits", () => {
  const catalog = { creditsPerMinute: 8, savedTranscriptCreditsPerMinute: 16 };
  assert.equal(getEffectiveCreditsPerMinute(catalog, false), 8);
  assert.equal(getEffectiveCreditsPerMinute(catalog, true), 16);
  assert.equal(getEffectiveCreditsPerMinute({
    ...catalog,
    modes: [{ code: "scribe", creditsPerMinute: 6 }],
  }, true, "scribe"), 6);
});

test("enhanced sessions omit speech-recognition-only fields", () => {
  assert.deepEqual(buildTranscriptSessionRequest({
    targetLanguage: "sv",
    translationMode: "enhanced",
    sourceLanguage: "pl",
    saveTranscript: false,
  }), {
    targetLanguage: "sv",
    translationMode: "enhanced",
    saveTranscript: false,
  });
});

test("the transcript toggle is not gated by active state or language matching", () => {
  const catalog = { savedSourceTranscriptsEnabled: true };

  assert.equal(isTranscriptToggleDisabled({ busy: false, active: true, catalog }), false);
  assert.equal(isTranscriptToggleDisabled({ busy: true, active: false, catalog }), true);
  assert.equal(isTranscriptToggleDisabled({
    busy: false,
    active: false,
    catalog: { savedSourceTranscriptsEnabled: false },
  }), true);
});

test("an unavailable saved mode falls back to Scribe before Enhanced", () => {
  const modes = [
    { code: "scribe", name: "ElevenLabs Scribe v2" },
    { code: "enhanced", name: "Enhanced" },
  ];
  assert.equal(selectAvailableTranslationMode(modes, "economical"), "scribe");
  assert.equal(selectAvailableTranslationMode(modes, "enhanced"), "enhanced");
  assert.equal(selectAvailableTranslationMode([{ code: "enhanced" }], "scribe"), "enhanced");
});
