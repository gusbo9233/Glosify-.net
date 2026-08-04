import test from "node:test";
import assert from "node:assert/strict";
import {
  buildTranscriptSessionRequest,
  canEnableSourceTranscript,
  canSaveSourceTranscript,
  clearTranscriptStorageState,
  getEffectiveCreditsPerMinute,
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
    transcriptId: "2145953e-a101-40fb-9859-6bd80225695e",
  });
});

test("stopping clears consent and transcript identity", () => {
  const state = clearTranscriptStorageState({ saveTranscript: true, transcriptId: "saved" });
  assert.equal(state.saveTranscript, false);
  assert.equal(state.transcriptId, null);
});

test("source saving requires feature enablement and an exact selected-language match", () => {
  const catalog = {
    savedSourceTranscriptsEnabled: true,
    selectedQuizLanguage: { code: "pl", name: "Polish" },
  };
  assert.equal(canSaveSourceTranscript(catalog, "pl"), true);
  assert.equal(canSaveSourceTranscript(catalog, "de"), false);
  assert.equal(canSaveSourceTranscript({ ...catalog, savedSourceTranscriptsEnabled: false }, "pl"), false);
  assert.equal(canSaveSourceTranscript({ ...catalog, selectedQuizLanguage: null }, "pl"), false);
});

test("the save checkbox can align any supported quiz target automatically", () => {
  const catalog = {
    savedSourceTranscriptsEnabled: true,
    quizLanguages: [
      { code: "et", name: "Estonian" },
      { code: "de", name: "German" },
      { code: "pl", name: "Polish" },
      { code: "uk", name: "Ukrainian" },
    ],
    selectedQuizLanguage: { code: "pl", name: "Polish" },
  };

  assert.equal(canEnableSourceTranscript(catalog, "de"), true);
  assert.equal(canSaveSourceTranscript(catalog, "de"), false);
  assert.equal(canEnableSourceTranscript(catalog, "es"), false);
});

test("source saving switches the displayed and preflight rate to sixteen credits", () => {
  const catalog = { creditsPerMinute: 8, savedTranscriptCreditsPerMinute: 16 };
  assert.equal(getEffectiveCreditsPerMinute(catalog, false), 8);
  assert.equal(getEffectiveCreditsPerMinute(catalog, true), 16);
});
