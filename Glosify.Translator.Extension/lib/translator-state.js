(function initializeTranslatorState(global) {
  const api = Object.freeze({
    normalizeSettings(settings, catalog) {
      const languages = catalog?.languages ?? [];
      const sourceLanguages = catalog?.sourceLanguages ?? [];
      const targetLanguage = languages.some(item => item.code === settings?.targetLanguage)
        ? settings.targetLanguage
        : languages.find(item => item.code === "en")?.code ?? languages[0]?.code ?? "en";
      const sourceLanguage = sourceLanguages.some(item => item.code === settings?.sourceLanguage)
        ? settings.sourceLanguage
        : "auto";
      return {
        sourceLanguage,
        targetLanguage,
        preferences: String(settings?.preferences ?? "").slice(0, catalog?.maxPreferenceCharacters ?? 500),
      };
    },
    invalidate(state) {
      return { ...state, result: null, requestId: null, saved: false };
    },
    canTranslate(state) {
      return Boolean(state?.sourceText?.trim())
        && state.sourceLanguage !== state.targetLanguage
        && !state.busy;
    },
    swap(state) {
      const effectiveSource = state.sourceLanguage === "auto"
        ? state.result?.detectedSourceLanguage
        : state.sourceLanguage;
      if (!effectiveSource || effectiveSource === state.targetLanguage || !state.result) {
        return state;
      }
      return {
        ...state,
        sourceLanguage: state.targetLanguage,
        targetLanguage: effectiveSource,
        sourceText: state.result.translatedText,
        result: null,
        requestId: null,
        saved: false,
      };
    },
    parseProblem(problem, status = 0) {
      return {
        status,
        message: problem?.detail ?? problem?.title ?? problem?.error ?? `Glosify request failed (${status}).`,
        code: problem?.code ?? null,
      };
    },
  });
  global.GlosifyTranslatorState = api;
})(globalThis);
