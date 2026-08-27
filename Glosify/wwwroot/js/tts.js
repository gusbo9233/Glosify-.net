(function () {
    'use strict';

    // Prefer Azure Speech via /api/tts. Unsupported languages and failed
    // requests fall back to SpeechSynthesis without affecting supported voices.
    var azureUnavailable = false;
    var unsupportedAzureLanguages = new Set();
    var currentSession = null;
    var nextSessionId = 0;

    // The server derives this map from the canonical quiz-language catalog and
    // Azure voice map so browser fallback cannot drift onto the OS default voice.
    var LOCALE_MAP = {};
    try {
        var configuredLocales = JSON.parse(document.body?.dataset.ttsLocales || '{}');
        Object.keys(configuredLocales).forEach(function (key) {
            LOCALE_MAP[key.toLowerCase()] = configuredLocales[key];
        });
    } catch (error) {
        console.warn('TTS locale configuration could not be read.', error);
    }

    function normalizeLocale(lang) {
        if (!lang) return '';
        var key = String(lang).trim().toLowerCase();
        if (LOCALE_MAP[key]) return LOCALE_MAP[key];
        // Already looks like BCP-47 (e.g. "pt-BR"): preserve casing convention.
        var match = key.match(/^([a-z]{2,3})[-_]([a-z]{2,4})$/);
        if (match) return match[1] + '-' + match[2].toUpperCase();
        return key;
    }

    function cancellationError() {
        var error = new Error('Speech playback was stopped.');
        error.name = 'TtsPlaybackCancelledError';
        return error;
    }

    function isCancellation(error) {
        return error && (error.name === 'TtsPlaybackCancelledError' || error.name === 'AbortError');
    }

    function safeCall(callback) {
        if (typeof callback !== 'function') return;
        try {
            callback.apply(null, Array.prototype.slice.call(arguments, 1));
        } catch (error) {
            console.error('TTS callback failed.', error);
        }
    }

    function ensureCurrent(session) {
        if (session.cancelled || currentSession !== session) {
            throw cancellationError();
        }
    }

    function releaseAudio(session) {
        if (session.audio) {
            session.audio.onended = null;
            session.audio.onerror = null;
            try { session.audio.pause(); } catch { /* ignore */ }
            session.audio.removeAttribute('src');
            session.audio = null;
        }
        if (session.objectUrl) {
            URL.revokeObjectURL(session.objectUrl);
            session.objectUrl = null;
        }
    }

    function releaseBrowserSpeech(session) {
        session.utterance = null;
        if (window.speechSynthesis) {
            window.speechSynthesis.cancel();
        }
    }

    function releaseCurrentItem(session) {
        if (session.abortController) {
            session.abortController.abort();
            session.abortController = null;
        }
        if (session.cancelCurrent) {
            var cancel = session.cancelCurrent;
            session.cancelCurrent = null;
            cancel();
        }
        releaseAudio(session);
        releaseBrowserSpeech(session);
    }

    function finalize(session, state, error) {
        if (session.finished) return;
        session.finished = true;
        releaseCurrentItem(session);
        if (currentSession === session) currentSession = null;
        safeCall(session.callbacks.onStateChange, state, error || null);
        session.resolve({ state: state, error: error || null });
    }

    function stop() {
        if (!currentSession) return false;
        var session = currentSession;
        session.cancelled = true;
        finalize(session, 'stopped');
        return true;
    }

    function pickVoice(locale) {
        if (!window.speechSynthesis) return null;
        var voices = window.speechSynthesis.getVoices();
        if (!voices || !voices.length) return null;
        var lower = locale.toLowerCase();
        var langOnly = lower.split('-')[0];
        var exact = voices.filter(function (voice) {
            return voice.lang && voice.lang.toLowerCase() === lower;
        });
        if (exact.length) {
            return exact.find(function (voice) { return voice.localService; }) || exact[0];
        }
        var sameLanguage = voices.filter(function (voice) {
            return voice.lang && voice.lang.toLowerCase().split('-')[0] === langOnly;
        });
        if (sameLanguage.length) {
            return sameLanguage.find(function (voice) { return voice.localService; }) || sameLanguage[0];
        }
        return null;
    }

    function ensureVoicesReady(session) {
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
            setTimeout(finish, 500);
        }).then(function () { ensureCurrent(session); });
    }

    async function playBrowser(item, session) {
        if (!window.speechSynthesis || typeof window.SpeechSynthesisUtterance !== 'function') {
            throw new Error('Text-to-speech is unavailable in this browser.');
        }
        await ensureVoicesReady(session);
        ensureCurrent(session);

        var locale = normalizeLocale(item.lang);
        var utterance = new SpeechSynthesisUtterance(item.text);
        session.utterance = utterance;
        if (locale) {
            utterance.lang = locale;
            var voice = pickVoice(locale);
            if (voice) utterance.voice = voice;
        }

        await new Promise(function (resolve, reject) {
            var settled = false;
            var finish = function (error) {
                if (settled) return;
                settled = true;
                utterance.onend = null;
                utterance.onerror = null;
                if (session.cancelCurrent === cancel) session.cancelCurrent = null;
                session.utterance = null;
                if (error) reject(error); else resolve();
            };
            var cancel = function () { finish(cancellationError()); };
            session.cancelCurrent = cancel;
            utterance.onend = function () { finish(); };
            utterance.onerror = function () {
                finish(new Error('Browser text-to-speech playback failed.'));
            };
            window.speechSynthesis.speak(utterance);
        });
    }

    async function playAzure(item, session) {
        var url = '/api/tts?text=' + encodeURIComponent(item.text)
            + '&lang=' + encodeURIComponent(item.lang);
        if (item.quality) url += '&quality=' + encodeURIComponent(item.quality);
        if (item.voice) url += '&voice=' + encodeURIComponent(item.voice);
        var controller = new AbortController();
        session.abortController = controller;
        var response;
        try {
            response = await fetch(url, {
                credentials: 'same-origin',
                signal: controller.signal,
            });
        } finally {
            if (session.abortController === controller) session.abortController = null;
        }
        ensureCurrent(session);

        var languageKey = normalizeLocale(item.lang) || String(item.lang || '').trim().toLowerCase();
        if (response.status === 501) {
            unsupportedAzureLanguages.add(languageKey);
            throw new Error('Azure Speech does not support this language.');
        }
        if (response.status === 503) {
            azureUnavailable = true;
            throw new Error('Azure Speech is not configured.');
        }
        if (!response.ok) {
            throw new Error('TTS request failed: ' + response.status);
        }

        var blob = await response.blob();
        ensureCurrent(session);
        var objectUrl = URL.createObjectURL(blob);
        var audio = new Audio(objectUrl);
        session.audio = audio;
        session.objectUrl = objectUrl;

        try {
            await new Promise(function (resolve, reject) {
                var settled = false;
                var finish = function (error) {
                    if (settled) return;
                    settled = true;
                    audio.onended = null;
                    audio.onerror = null;
                    if (session.cancelCurrent === cancel) session.cancelCurrent = null;
                    if (error) reject(error); else resolve();
                };
                var cancel = function () { finish(cancellationError()); };
                session.cancelCurrent = cancel;
                audio.onended = function () { finish(); };
                audio.onerror = function () { finish(new Error('Audio playback failed.')); };
                audio.play().catch(finish);
            });
        } finally {
            releaseAudio(session);
        }
    }

    async function playItem(item, session) {
        var languageKey = normalizeLocale(item.lang) || String(item.lang || '').trim().toLowerCase();
        var useBrowser = !item.lang
            || azureUnavailable
            || session.azureFailed
            || unsupportedAzureLanguages.has(languageKey);
        if (useBrowser && item.voice) {
            throw new Error('The selected Azure voice is unavailable. No browser voice was substituted.');
        }
        if (!useBrowser) {
            try {
                await playAzure(item, session);
                return;
            } catch (error) {
                if (isCancellation(error)) throw error;
                if (item.voice) {
                    throw new Error(
                        'The selected Azure voice could not be played. No browser voice was substituted.',
                        { cause: error });
                }
                session.azureFailed = true;
                console.warn('Azure TTS failed, falling back to browser TTS.', error);
            }
        }
        ensureCurrent(session);
        await playBrowser(item, session);
    }

    async function runQueue(session) {
        try {
            for (var index = 0; index < session.items.length; index += 1) {
                ensureCurrent(session);
                var item = session.items[index];
                safeCall(session.callbacks.onItemStart, item, index, session.items.length);
                await playItem(item, session);
                ensureCurrent(session);
                safeCall(session.callbacks.onItemEnd, item, index, session.items.length);
            }
            finalize(session, 'completed');
        } catch (error) {
            if (isCancellation(error) || session.cancelled) return;
            finalize(session, 'error', error);
        }
    }

    function playQueue(items, callbacks) {
        stop();
        callbacks = callbacks || {};
        var normalizedItems = (Array.isArray(items) ? items : [])
            .map(function (item) {
                return {
                    text: String(item && item.text || '').trim(),
                    lang: String(item && item.lang || '').trim(),
                    quality: String(item && item.quality || '').trim(),
                    voice: String(item && item.voice || '').trim(),
                    meta: item && item.meta,
                };
            })
            .filter(function (item) { return item.text.length > 0; });

        if (!normalizedItems.length) {
            var emptyError = new Error('No text is available to read.');
            safeCall(callbacks.onStateChange, 'error', emptyError);
            return Promise.resolve({ state: 'error', error: emptyError });
        }

        var resolveResult;
        var result = new Promise(function (resolve) { resolveResult = resolve; });
        var session = {
            id: ++nextSessionId,
            items: normalizedItems,
            callbacks: callbacks,
            resolve: resolveResult,
            cancelled: false,
            finished: false,
            abortController: null,
            cancelCurrent: null,
            audio: null,
            objectUrl: null,
            utterance: null,
            azureFailed: false,
        };
        currentSession = session;
        safeCall(callbacks.onStateChange, 'playing', null);
        runQueue(session);
        return result;
    }

    window.GlosifyTts = Object.freeze({
        playQueue: playQueue,
        stop: stop,
    });

    document.addEventListener('click', function (event) {
        var button = event.target.closest('[data-tts]');
        if (!button) return;
        event.preventDefault();

        var text = button.getAttribute('data-tts');
        var lang = button.getAttribute('data-tts-lang') || '';
        if (!text) return;

        var wasPlaying = button.classList.contains('is-playing');
        stop();
        if (wasPlaying) return;

        playQueue([{ text: text, lang: lang }], {
            onStateChange: function (state, error) {
                button.classList.toggle('is-playing', state === 'playing');
                button.setAttribute('aria-pressed', String(state === 'playing'));
                if (state === 'error') console.warn('Text-to-speech failed.', error);
            },
        });
    });
})();
