export function buildTranscriptSessionRequest({
  targetLanguage,
  translationMode,
  sourceLanguage,
  partialCaptionsEnabled = true,
  saveTranscript,
  transcriptId = null,
}) {
  const request = {
    targetLanguage,
    saveTranscript: Boolean(saveTranscript),
    partialCaptionsEnabled: !isScribeTranslationMode(translationMode)
      || partialCaptionsEnabled !== false,
  };
  if (translationMode) {
    request.translationMode = translationMode;
  }
  if (isScribeTranslationMode(translationMode) || saveTranscript) {
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
  if (!catalog) {
    return null;
  }
  if (translationMode !== "enhanced") {
    return positivePrice(catalog.modes?.find(mode => mode.code === translationMode)?.creditsPerMinute);
  }
  return saveTranscript
    ? positivePrice(catalog.savedTranscriptCreditsPerMinute)
    : positivePrice(catalog.modes?.find(mode => mode.code === "enhanced")?.creditsPerMinute
      ?? catalog.creditsPerMinute);
}

export function isScribeTranslationMode(translationMode) {
  return translationMode === "scribe" || translationMode === "scribe-cf";
}

function positivePrice(value) {
  return Number.isFinite(value) && value > 0 ? value : null;
}

export function isTranscriptToggleDisabled({ busy, catalog }) {
  return Boolean(busy || catalog?.savedSourceTranscriptsEnabled === false);
}

export function selectAvailableTranslationMode(modes, requestedMode) {
  if (modes?.some(mode => mode.code === requestedMode)) {
    return requestedMode;
  }
  return modes?.find(mode => mode.code === "scribe-cf")?.code
    ?? modes?.find(mode => mode.code === "enhanced")?.code
    ?? modes?.[0]?.code
    ?? "enhanced";
}

export function selectCurrentTranslationModes(modes) {
  const available = Array.isArray(modes) ? modes : [];
  return available.some(mode => mode.code === "scribe-cf")
    ? available.filter(mode => mode.code !== "scribe")
    : available;
}
