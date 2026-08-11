export function buildTranscriptSessionRequest({
  targetLanguage,
  translationMode,
  sourceLanguage,
  saveTranscript,
  transcriptId = null,
}) {
  const request = {
    targetLanguage,
    saveTranscript: Boolean(saveTranscript),
  };
  if (translationMode) {
    request.translationMode = translationMode;
  }
  if (translationMode === "economical") {
    request.sourceLanguage = sourceLanguage;
  }
  if (request.saveTranscript && transcriptId) {
    request.transcriptId = transcriptId;
  }
  return request;
}

export function clearTranscriptStorageState(state) {
  state.saveTranscript = false;
  state.transcriptId = null;
  return state;
}

export function canSaveSourceTranscript(catalog) {
  return Boolean(catalog?.savedSourceTranscriptsEnabled
    && catalog.selectedQuizLanguage);
}

export function getEffectiveCreditsPerMinute(catalog, saveTranscript, translationMode = "enhanced") {
  if (translationMode === "economical") {
    return catalog?.modes?.find(mode => mode.code === "economical")?.creditsPerMinute ?? 4;
  }
  return saveTranscript
    ? catalog?.savedTranscriptCreditsPerMinute ?? 16
    : catalog?.creditsPerMinute ?? 8;
}

export function isTranscriptToggleDisabled({ busy, catalog }) {
  return Boolean(busy || catalog?.savedSourceTranscriptsEnabled === false);
}
