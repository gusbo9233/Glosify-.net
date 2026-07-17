(function () {
    'use strict';

    // Prefer Azure Speech via /api/tts. On 501/503/network errors, fall back to
    // window.speechSynthesis so the button still works without a Speech resource.
    var azureUnavailable = false;
    var currentAudio = null;
    var currentObjectUrl = null;
    var currentButton = null;

    // Map free-form language names / short codes from the quiz to BCP-47 locales
    // so the browser's SpeechSynthesis picks the right voice instead of the OS default.
    var LOCALE_MAP = {
        'et': 'et-EE', 'et-ee': 'et-EE', 'estonian': 'et-EE',
        'de': 'de-DE', 'de-de': 'de-DE', 'german': 'de-DE',
        'pl': 'pl-PL', 'pl-pl': 'pl-PL', 'polish': 'pl-PL',
        'uk': 'uk-UA', 'uk-ua': 'uk-UA', 'ukrainian': 'uk-UA',
        'en': 'en-US', 'en-us': 'en-US', 'english': 'en-US',
        'sv': 'sv-SE', 'sv-se': 'sv-SE', 'swedish': 'sv-SE',
        'fr': 'fr-FR', 'fr-fr': 'fr-FR', 'french': 'fr-FR',
        'es': 'es-ES', 'es-es': 'es-ES', 'spanish': 'es-ES',
        'it': 'it-IT', 'it-it': 'it-IT', 'italian': 'it-IT',
    };

    function normalizeLocale(lang) {
        if (!lang) return '';
        var key = String(lang).trim().toLowerCase();
        if (LOCALE_MAP[key]) return LOCALE_MAP[key];
        // Already looks like BCP-47 (e.g. "pt-BR"): preserve casing convention.
        var m = key.match(/^([a-z]{2,3})[-_]([a-z]{2,4})$/);
        if (m) return m[1] + '-' + m[2].toUpperCase();
        return key;
    }

    function stopCurrent() {
        if (currentAudio) {
            currentAudio.onended = null;
            currentAudio.onerror = null;
            try { currentAudio.pause(); } catch (e) { /* ignore */ }
            currentAudio.removeAttribute('src');
            currentAudio = null;
        }
        if (currentObjectUrl) {
            URL.revokeObjectURL(currentObjectUrl);
            currentObjectUrl = null;
        }
        if (window.speechSynthesis) {
            window.speechSynthesis.cancel();
        }
        if (currentButton) {
            currentButton.classList.remove('is-playing');
            currentButton = null;
        }
    }

    function markPlaying(button) {
        currentButton = button;
        button.classList.add('is-playing');
    }

    function pickVoice(locale) {
        if (!window.speechSynthesis) return null;
        var voices = window.speechSynthesis.getVoices();
        if (!voices || !voices.length) return null;
        var lower = locale.toLowerCase();
        var langOnly = lower.split('-')[0];
        // 1. Exact BCP-47 match, prefer localService (offline, usually higher quality).
        var exact = voices.filter(function (v) { return v.lang && v.lang.toLowerCase() === lower; });
        if (exact.length) {
            return exact.find(function (v) { return v.localService; }) || exact[0];
        }
        // 2. Same primary language (e.g. any pl-*).
        var sameLang = voices.filter(function (v) {
            return v.lang && v.lang.toLowerCase().split('-')[0] === langOnly;
        });
        if (sameLang.length) {
            return sameLang.find(function (v) { return v.localService; }) || sameLang[0];
        }
        return null;
    }

    // On some browsers (notably Chrome) getVoices() is empty until the
    // voiceschanged event fires. Prime the list once.
    function ensureVoicesReady() {
        return new Promise(function (resolve) {
            if (!window.speechSynthesis) { resolve(); return; }
            var voices = window.speechSynthesis.getVoices();
            if (voices && voices.length) { resolve(); return; }
            var done = false;
            var finish = function () {
                if (done) return;
                done = true;
                window.speechSynthesis.removeEventListener('voiceschanged', finish);
                resolve();
            };
            window.speechSynthesis.addEventListener('voiceschanged', finish);
            // Fallback timeout so callers never hang if the event never fires.
            setTimeout(finish, 500);
        });
    }

    async function playBrowser(text, lang, button) {
        if (!window.speechSynthesis || typeof window.SpeechSynthesisUtterance !== 'function') {
            button.classList.remove('is-playing');
            return;
        }
        await ensureVoicesReady();
        var locale = normalizeLocale(lang);
        var utter = new SpeechSynthesisUtterance(text);
        if (locale) {
            utter.lang = locale;
            var voice = pickVoice(locale);
            if (voice) {
                utter.voice = voice;
            } else {
                console.info('No SpeechSynthesis voice found for', locale,
                    '- browser will use its default voice.');
            }
        }
        var finish = function () {
            button.classList.remove('is-playing');
            if (currentButton === button) currentButton = null;
        };
        utter.onend = finish;
        utter.onerror = finish;
        window.speechSynthesis.speak(utter);
    }

    async function playAzure(text, lang, button) {
        var url = '/api/tts?text=' + encodeURIComponent(text) + '&lang=' + encodeURIComponent(lang);
        var response = await fetch(url, { credentials: 'same-origin' });
        if (response.status === 503 || response.status === 501) {
            azureUnavailable = true;
            await playBrowser(text, lang, button);
            return;
        }
        if (!response.ok) {
            throw new Error('TTS request failed: ' + response.status);
        }
        var blob = await response.blob();
        var objectUrl = URL.createObjectURL(blob);
        var audio = new Audio(objectUrl);
        currentAudio = audio;
        currentObjectUrl = objectUrl;
        var released = false;
        var release = function () {
            if (released) return;
            released = true;
            button.classList.remove('is-playing');
            if (currentAudio === audio) {
                currentAudio = null;
                currentButton = null;
            }
            if (currentObjectUrl === objectUrl) {
                currentObjectUrl = null;
            }
            audio.removeAttribute('src');
            URL.revokeObjectURL(objectUrl);
        };
        audio.onended = release;
        audio.onerror = release;
        try {
            await audio.play();
        } catch (error) {
            release();
            throw error;
        }
    }

    document.addEventListener('click', async function (event) {
        var button = event.target.closest('[data-tts]');
        if (!button) {
            return;
        }
        event.preventDefault();

        var text = button.getAttribute('data-tts');
        var lang = button.getAttribute('data-tts-lang') || '';
        if (!text) {
            return;
        }

        var wasPlaying = button.classList.contains('is-playing');
        stopCurrent();
        if (wasPlaying) {
            return;
        }

        markPlaying(button);

        if (azureUnavailable) {
            await playBrowser(text, lang, button);
            return;
        }

        try {
            await playAzure(text, lang, button);
        } catch (err) {
            console.warn('Azure TTS failed, falling back to browser TTS.', err);
            markPlaying(button);
            await playBrowser(text, lang, button);
        }
    });
})();
