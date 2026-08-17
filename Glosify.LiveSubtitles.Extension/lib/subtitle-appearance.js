(() => {
  const STORAGE_KEY = "glosifyTransparentSubtitles";

  function normalizeTransparentSubtitles(value) {
    return value === true;
  }

  function applyTransparentSubtitles(panel, value) {
    const enabled = normalizeTransparentSubtitles(value);
    panel.classList.toggle("transparent", enabled);
    return enabled;
  }

  globalThis.GlosifySubtitleAppearance = Object.freeze({
    STORAGE_KEY,
    normalizeTransparentSubtitles,
    applyTransparentSubtitles,
  });
})();
