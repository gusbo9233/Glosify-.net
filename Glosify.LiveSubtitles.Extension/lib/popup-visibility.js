export function setQuizLanguageVisibility(group, saveTranscript) {
  group.classList.toggle("hidden", !saveTranscript);
}

export function setPartialCaptionsVisibility(group, translationMode) {
  group.classList.toggle("hidden", !["scribe", "scribe-cf"].includes(translationMode));
}
