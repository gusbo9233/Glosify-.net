import test from "node:test";
import assert from "node:assert/strict";
import {
  buildTranscriptSessionRequest,
  canSaveSourceTranscript,
  clearTranscriptStorageState,
  getEffectiveCreditsPerMinute,
  isTranscriptToggleDisabled,
  selectAvailableTranslationMode,
  selectCurrentTranslationModes,
  isScribeTranslationMode,
} from "../lib/transcript-storage.js";

test("transcript storage is opt-in and does not send an id while disabled", () => {
  assert.deepEqual(buildTranscriptSessionRequest({
    targetLanguage: "es",
    saveTranscript: false,
    transcriptId: "ignored",
  }), {
    targetLanguage: "es",
    saveTranscript: false,
    partialCaptionsEnabled: true,
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
    partialCaptionsEnabled: true,
    sourceLanguage: "auto",
    transcriptId: "2145953e-a101-40fb-9859-6bd80225695e",
  });
});

test("speech recognition modes include mode and source language", () => {
  assert.deepEqual(buildTranscriptSessionRequest({
    targetLanguage: "sv",
    translationMode: "scribe-cf",
    sourceLanguage: "pl",
    saveTranscript: false,
  }), {
    targetLanguage: "sv",
    translationMode: "scribe-cf",
    sourceLanguage: "pl",
    saveTranscript: false,
    partialCaptionsEnabled: true,
  });
});

test("Cloudflare Scribe carries partial preference and spoken-language hint", () => {
  assert.deepEqual(buildTranscriptSessionRequest({
    targetLanguage: "fr",
    translationMode: "scribe-cf",
    sourceLanguage: "en",
    partialCaptionsEnabled: false,
    saveTranscript: false,
  }), {
    targetLanguage: "fr",
    translationMode: "scribe-cf",
    sourceLanguage: "en",
    saveTranscript: false,
    partialCaptionsEnabled: false,
  });
  assert.equal(isScribeTranslationMode("scribe-cf"), true);
  assert.equal(getEffectiveCreditsPerMinute({
    modes: [{ code: "scribe-cf", creditsPerMinute: 4 }],
  }, false, "scribe-cf"), 4);
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
    modes: [{ code: "scribe-cf", creditsPerMinute: 6 }],
  }, true, "scribe-cf"), 6);
});

test("missing or invalid catalog prices never fall back to hardcoded charges", () => {
  assert.equal(getEffectiveCreditsPerMinute(null, false), null);
  assert.equal(getEffectiveCreditsPerMinute({}, false), null);
  assert.equal(getEffectiveCreditsPerMinute({ creditsPerMinute: 0 }, false), null);
  assert.equal(getEffectiveCreditsPerMinute({
    modes: [{ code: "scribe-cf", creditsPerMinute: -1 }],
  }, false, "scribe-cf"), null);
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
    partialCaptionsEnabled: true,
  });
});

test("sessions explicitly carry a disabled partial-caption preference", () => {
  assert.deepEqual(buildTranscriptSessionRequest({
    targetLanguage: "de",
    translationMode: "scribe-cf",
    partialCaptionsEnabled: false,
    saveTranscript: false,
  }), {
    targetLanguage: "de",
    translationMode: "scribe-cf",
    sourceLanguage: "auto",
    saveTranscript: false,
    partialCaptionsEnabled: false,
  });
});

test("Enhanced sessions always keep streaming partial captions", () => {
  assert.equal(buildTranscriptSessionRequest({
    targetLanguage: "sv",
    translationMode: "enhanced",
    partialCaptionsEnabled: false,
    saveTranscript: false,
  }).partialCaptionsEnabled, true);
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

test("an unavailable saved mode falls back to Cloudflare Scribe before Enhanced", () => {
  const modes = [
    { code: "scribe-cf", name: "Scribe + Cloudflare" },
    { code: "enhanced", name: "Enhanced" },
  ];
  assert.equal(selectAvailableTranslationMode(modes, "scribe"), "scribe-cf");
  assert.equal(selectAvailableTranslationMode(modes, "enhanced"), "enhanced");
  assert.equal(selectAvailableTranslationMode([{ code: "enhanced" }], "scribe"), "enhanced");
});

test("current clients hide the legacy Scribe compatibility alias", () => {
  const modes = [
    { code: "scribe", name: "Scribe + Cloudflare" },
    { code: "scribe-cf", name: "Scribe + Cloudflare" },
    { code: "enhanced", name: "Enhanced" },
  ];

  assert.deepEqual(
    selectCurrentTranslationModes(modes).map(mode => mode.code),
    ["scribe-cf", "enhanced"]);
  assert.deepEqual(
    selectCurrentTranslationModes([{ code: "scribe" }]).map(mode => mode.code),
    ["scribe"]);
});
