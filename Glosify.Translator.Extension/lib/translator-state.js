(function initializeTranslatorState(global) {
  const api = Object.freeze({
    normalizeSettingsForStorage(settings, catalog) {
      if (catalog) return api.normalizeSettings(settings, catalog);

      return {
        sourceLanguage: boundedLanguageCode(settings?.sourceLanguage, "auto"),
        targetLanguage: boundedLanguageCode(settings?.targetLanguage, "en"),
        preferences: String(settings?.preferences ?? "").slice(0, 500),
      };
    },
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
      return { ...state, result: null, requestId: null, saveLanguage: null, saved: false };
    },
    createRequestId(cryptoApi = global.crypto) {
      if (typeof cryptoApi?.getRandomValues !== "function") {
        throw new Error("Secure random number generation is unavailable.");
      }

      const bytes = cryptoApi.getRandomValues(new Uint8Array(16));
      bytes[6] = (bytes[6] & 0x0f) | 0x40;
      bytes[8] = (bytes[8] & 0x3f) | 0x80;
      const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
      return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
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
        saveLanguage: null,
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

  function boundedLanguageCode(value, fallback) {
    const candidate = typeof value === "string" ? value.trim() : "";
    return /^(?:auto|[a-z]{2,3}(?:-[a-z]{4})?)$/iu.test(candidate)
      ? candidate
      : fallback;
  }

  global.GlosifyTranslatorState = api;
})(globalThis);
