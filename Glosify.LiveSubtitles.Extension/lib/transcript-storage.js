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
  if (translationMode === "scribe" || saveTranscript) {
    request.sourceLanguage = sourceLanguage || "auto";
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

export function getEffectiveCreditsPerMinute(
  catalog,
  saveTranscript,
  translationMode = "enhanced") {
  if (translationMode === "scribe") {
    return catalog?.modes?.find(mode => mode.code === translationMode)?.creditsPerMinute
      ?? (translationMode === "scribe" ? 6 : 4);
  }
  return saveTranscript
    ? catalog?.savedTranscriptCreditsPerMinute ?? 16
    : catalog?.creditsPerMinute ?? 8;
}

export function isTranscriptToggleDisabled({ busy, catalog }) {
  return Boolean(busy || catalog?.savedSourceTranscriptsEnabled === false);
}

export function selectAvailableTranslationMode(modes, requestedMode) {
  if (modes?.some(mode => mode.code === requestedMode)) {
    return requestedMode;
  }
  return modes?.find(mode => mode.code === "scribe")?.code
    ?? modes?.find(mode => mode.code === "enhanced")?.code
    ?? modes?.[0]?.code
    ?? "enhanced";
}
