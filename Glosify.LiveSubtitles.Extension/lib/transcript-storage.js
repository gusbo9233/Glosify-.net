export function buildTranscriptSessionRequest({ targetLanguage, saveTranscript, transcriptId = null }) {
  const request = {
    targetLanguage,
    saveTranscript: Boolean(saveTranscript),
  };
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

export function getEffectiveCreditsPerMinute(catalog, saveTranscript) {
  return saveTranscript
    ? catalog?.savedTranscriptCreditsPerMinute ?? 16
    : catalog?.creditsPerMinute ?? 8;
}

export function isTranscriptToggleDisabled({ busy, catalog }) {
  return Boolean(busy || catalog?.savedSourceTranscriptsEnabled === false);
}
