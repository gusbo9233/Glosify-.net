(function () {
    'use strict';

    var currentSession = null;
    var nextSessionId = 0;

    // The server derives this map from the canonical quiz-language catalog and
    // ElevenLabs voice map so browser speech cannot drift onto the OS default voice.
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

    function sameSpeechLanguage(first, second) {
        // A generic browser tag is ambiguous even when the content alias maps zh to Mandarin.
        if (String(first || '').trim().toLowerCase() === 'zh' || String(second || '').trim().toLowerCase() === 'zh') return false;
        var a = normalizeLocale(first).toLowerCase();
        var b = normalizeLocale(second).toLowerCase();
        // Cantonese and Mandarin must not be conflated as generic "zh".
        return a === b || (a && b && a.split('-')[0] !== 'zh' && b.split('-')[0] !== 'zh' && a.split('-')[0] === b.split('-')[0]);
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
        ++preparationRevision;
        if (pendingPreparation) {
            var pending = pendingPreparation;
            pendingPreparation = null;
            safeCall(pending.onStateChange, 'stopped', null);
        }
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
        var exact = voices.filter(function (voice) {
            return voice.lang && voice.lang.toLowerCase() === lower;
        });
        if (exact.length) {
            return exact.find(function (voice) { return voice.localService; }) || exact[0];
        }
        var sameLanguage = voices.filter(function (voice) {
            return voice.lang && sameSpeechLanguage(voice.lang, locale);
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
        }).then(function () { if (session) ensureCurrent(session); });
    }

    async function playBrowser(item, session) {
        if (!window.speechSynthesis || typeof window.SpeechSynthesisUtterance !== 'function') {
            throw new Error('Text-to-speech is unavailable in this browser.');
        }
        await ensureVoicesReady(session);
        ensureCurrent(session);

        var locale = normalizeLocale(item.lang);
        if (!locale) throw new Error('Choose the language of the text before playback.');
        var utterance = new SpeechSynthesisUtterance(item.text);
        session.utterance = utterance;
        if (locale) {
            utterance.lang = locale;
            var voice = item.voice
                ? window.speechSynthesis.getVoices().find(function (candidate) {
                    return candidate.voiceURI === item.voice && sameSpeechLanguage(candidate.lang, locale);
                })
                : pickVoice(locale);
            if (!voice) throw new Error('No browser voice is available for this language. Choose ElevenLabs or install a browser voice.');
            utterance.voice = voice;
            utterance.lang = voice.lang;
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

    async function playElevenLabs(item, session) {
        var controller = new AbortController();
        session.abortController = controller;
        var response;
        try {
            response = await fetch('/api/tts', {
                method: 'POST',
                credentials: 'same-origin',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': document.querySelector('[data-speech-token] input')?.value || '',
                },
                body: JSON.stringify({ text: item.text, lang: item.lang, voice: item.voice,
                    quality: item.quality, maxCredits: item.maxCredits }),
                signal: controller.signal,
            });
        } finally {
            if (session.abortController === controller) session.abortController = null;
        }
        ensureCurrent(session);

        if (!response.ok) {
            var detail = '';
            try { var problem = await response.json(); detail = problem.detail || problem.error || ''; } catch { /* Non-JSON response. */ }
            throw new Error(detail || 'ElevenLabs speech is unavailable. Try again or choose browser speech.');
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
        if (item.provider === 'elevenlabs') {
            await playElevenLabs(item, session);
        } else {
            await playBrowser(item, session);
        }
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
                    provider: item && item.provider === 'elevenlabs' ? 'elevenlabs' : 'browser',
                    maxCredits: Number(item && item.maxCredits) || 0,
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
        };
        currentSession = session;
        safeCall(callbacks.onStateChange, 'playing', null);
        runQueue(session);
        return result;
    }

    // Only explicit settings actions open the dialog. Playback reads committed preferences.
    var dialog = document.querySelector('[data-speech-dialog]');
    var choiceRevision = 0;
    var preparationRevision = 0;
    var pendingPreparation = null;
    var settingsContext = {};
    var lastContext = {};
    var preferences = {};
    var preferenceStorageKey = 'glosify.speech.preferences.user:' + encodeURIComponent(document.body?.dataset.speechUser || 'anonymous');
    try {
        var savedPreferences = localStorage.getItem(preferenceStorageKey);
        preferences = JSON.parse(savedPreferences || localStorage.getItem('glosify.speech.preferences') || '{}') || {};
        // Legacy preferences have no account owner. Retain voices, but require
        // this account to accept the rate explicitly before any ElevenLabs request.
        if (!savedPreferences) delete preferences.acceptedElevenLabsRate;
    } catch { /* Optional. */ }
    if (typeof preferences !== 'object' || Array.isArray(preferences)) preferences = {};
    if (preferences.provider === 'azure') {
        preferences.provider = 'browser';
        delete preferences.acceptedAzureRate;
        Object.keys(preferences).filter(key => key.startsWith('azure:')).forEach(key => delete preferences[key]);
    }
    var providerSelect = dialog?.querySelector('[data-speech-provider]');
    var languageSelect = dialog?.querySelector('[data-speech-language]');
    var voiceSelect = dialog?.querySelector('[data-speech-voice]');
    var choiceStatus = dialog?.querySelector('[data-speech-status]');
    var saveButton = dialog?.querySelector('[data-speech-save]');
    var quotedCredits = 0;
    var priceNotice = dialog?.querySelector('[data-speech-price]');
    var creditNotice = dialog?.querySelector('[data-speech-credits]');
    var errorNotice = document.querySelector('[data-reader-speech-error]') || document.querySelector('[data-speech-error]');
    var errorText = errorNotice?.querySelector('[data-speech-error-text]');
    var catalogCache = new Map();

    function getProvider() { return preferences.provider === 'elevenlabs' ? 'elevenlabs' : 'browser'; }
    function getBookLanguage(bookId) { return preferences.bookLanguages?.[bookId] || ''; }
    function message(key) { return dialog?.dataset[key] || key; }
    function reportError(error) {
        if (errorText) errorText.textContent = error.message;
        if (errorNotice) errorNotice.hidden = false;
    }
    async function voiceCatalog(provider, lang, fresh) {
        if (provider === 'browser') {
            await ensureVoicesReady();
            return { rate: 0, voices: (window.speechSynthesis?.getVoices() || [])
                .filter(function (voice) { return sameSpeechLanguage(voice.lang, lang); })
                .map(function (voice) { return { value: voice.voiceURI, label: voice.name + ' (' + voice.lang + ')' }; }) };
        }
        if (!fresh && catalogCache.has(lang)) return catalogCache.get(lang);
        var request = (async function () {
            var response = await fetch('/api/tts/voices?lang=' + encodeURIComponent(lang), { credentials: 'same-origin' });
            if (!response.ok) throw new Error(message('elevenlabsUnavailable'));
            var result = await response.json();
            if (!Number.isSafeInteger(result.creditsPerRequest) || result.creditsPerRequest < 1)
                throw new Error(message('elevenlabsUnavailable'));
            var maxTextLength = result.maxTextLength ?? 180;
            if (!Number.isSafeInteger(maxTextLength) || maxTextLength < 1) throw new Error(message('elevenlabsUnavailable'));
            return { rate: result.creditsPerRequest, maxTextLength: maxTextLength, voices: result.configured ? result.voices
                .filter(function (voice) { return sameSpeechLanguage(voice.locale, lang); })
                .map(function (voice) { return { value: voice.shortName, label: voice.displayName + ' (' + voice.locale + ')' }; }) : [] };
        })();
        catalogCache.set(lang, request);
        try { return await request; } catch (error) { if (catalogCache.get(lang) === request) catalogCache.delete(lang); throw error; }
    }
    async function loadVoiceChoices() {
        var revision = ++choiceRevision;
        var provider = providerSelect.value;
        var lang = languageSelect.value;
        voiceSelect.replaceChildren();
        saveButton.disabled = true;
        voiceSelect.disabled = true;
        creditNotice.hidden = provider !== 'elevenlabs';
        choiceStatus.textContent = message('loading');
        quotedCredits = 0;
        priceNotice.textContent = '';
        if (!lang) { choiceStatus.textContent = languageSelect.options[0].textContent; return; }
        try {
            var catalog = await voiceCatalog(provider, lang, true);
            if (revision !== choiceRevision || !dialog.open) return;
            quotedCredits = catalog.rate;
            priceNotice.textContent = message('rate').replace('{0}', String(quotedCredits));
            catalog.voices.forEach(function (voice) { voiceSelect.add(new Option(voice.label, voice.value)); });
            var saved = preferences[provider + ':' + lang];
            if (catalog.voices.some(function (voice) { return voice.value === saved; })) voiceSelect.value = saved;
            voiceSelect.disabled = !catalog.voices.length;
            saveButton.disabled = !catalog.voices.length;
            choiceStatus.textContent = catalog.voices.length ? '' : message(provider === 'elevenlabs' ? 'noElevenLabsVoices' : 'noBrowserVoices');
        } catch (error) { if (revision === choiceRevision) choiceStatus.textContent = error.message; }
    }

    function splitSpeechItems(items, maxTextLength) {
        var limit = Math.min(180, maxTextLength || 180);
        return items.flatMap(function (item) {
            var remaining = String(item.text || '').trim();
            var parts = [];
            while (remaining) {
                var end = remaining.length;
                if (end > limit) {
                    end = remaining.lastIndexOf(' ', limit);
                    if (end <= 0) end = limit;
                    var last = remaining.charCodeAt(end - 1);
                    if (last >= 0xD800 && last <= 0xDBFF) end -= 1;
                    if (!end) throw new Error(message('elevenlabsUnavailable'));
                }
                parts.push(Object.assign({}, item, { text: remaining.slice(0, end).trim() }));
                remaining = remaining.slice(end).trim();
            }
            return parts;
        });
    }

    function openSettings(context) {
        if (!dialog) return;
        stop();
        settingsContext = Object.assign({}, context || lastContext);
        var locale = normalizeLocale(settingsContext.lang || '');
        languageSelect.value = Array.from(languageSelect.options).some(function (option) { return option.value === locale; }) ? locale : '';
        providerSelect.value = getProvider();
        if (!dialog.open) dialog.showModal();
        loadVoiceChoices();
    }

    async function estimateQueue(items) {
        items = items.filter(function (item) { return String(item.text || '').trim(); });
        var provider = getProvider();
        var label = message(provider === 'elevenlabs' ? 'elevenlabsLabel' : 'browserLabel');
        if (provider !== 'elevenlabs' || !items.length) return label;
        try {
            var total = 0;
            for (var item of items) {
                var catalog = await voiceCatalog(provider, normalizeLocale(item.lang), false);
                total += catalog.rate * splitSpeechItems([item], catalog.maxTextLength).length;
            }
            return label + ' · ' + message('estimate').replace('{0}', String(total));
        } catch { return label; }
    }

    async function playSavedQueue(items, callbacks, context) {
        stop();
        var revision = preparationRevision;
        callbacks = callbacks || {};
        items = items.filter(function (item) { return String(item.text || '').trim(); });
        lastContext = Object.assign({ lang: items[0]?.lang || '' }, context);
        if (errorNotice) errorNotice.hidden = true;
        var provider = getProvider();
        // Mark preparation as active so a second Read click cancels it too.
        pendingPreparation = callbacks;
        safeCall(callbacks.onStateChange, 'playing', null);
        try {
            var catalogs = new Map();
            var prepared = [];
            for (var item of items) {
                var lang = normalizeLocale(item.lang);
                if (!catalogs.has(lang)) catalogs.set(lang, await voiceCatalog(provider, lang, true));
                if (revision !== preparationRevision) throw cancellationError();
                var catalog = catalogs.get(lang);
                if (provider === 'elevenlabs' && (!Number.isSafeInteger(preferences.acceptedElevenLabsRate)
                    || preferences.acceptedElevenLabsRate < catalog.rate)) throw new Error(message('reviewRate'));
                var saved = preferences[provider + ':' + lang];
                if (saved && !catalog.voices.some(function (voice) { return voice.value === saved; }))
                    throw new Error(message('voiceUnavailable'));
                if (!catalog.voices.length) throw new Error(message(provider === 'elevenlabs' ? 'noElevenLabsVoices' : 'noBrowserVoices'));
                prepared.push(...splitSpeechItems([Object.assign({}, item, { lang: lang, provider: provider,
                    voice: saved || catalog.voices[0].value, quality: '', maxCredits: catalog.rate })], catalog.maxTextLength));
            }
            if (revision !== preparationRevision) throw cancellationError();
            pendingPreparation = null;
            return await playQueue(prepared, Object.assign({}, callbacks, {
                onStateChange: function (state, error) {
                    if (state === 'error') reportError(error);
                    safeCall(callbacks.onStateChange, state, error);
                },
            }));
        } catch (error) {
            if (revision !== preparationRevision) return { state: 'stopped', error: null };
            pendingPreparation = null;
            var state = isCancellation(error) ? 'stopped' : 'error';
            if (state === 'error') reportError(error);
            safeCall(callbacks.onStateChange, state, state === 'error' ? error : null);
            return { state: state, error: state === 'error' ? error : null };
        }
    }

    providerSelect?.addEventListener('change', loadVoiceChoices);
    languageSelect?.addEventListener('change', loadVoiceChoices);
    window.speechSynthesis?.addEventListener('voiceschanged', function () {
        if (dialog?.open && providerSelect.value === 'browser') loadVoiceChoices();
    });
    dialog?.addEventListener('close', function () { if (!dialog.open) ++choiceRevision; });
    dialog?.querySelector('[data-speech-cancel]').addEventListener('click', function () { dialog.close(); });
    saveButton?.addEventListener('click', function () {
        if (!dialog.open || saveButton.disabled) return;
        preferences.provider = providerSelect.value;
        preferences[providerSelect.value + ':' + languageSelect.value] = voiceSelect.value;
        if (providerSelect.value === 'elevenlabs') preferences.acceptedElevenLabsRate = quotedCredits;
        if (settingsContext.bookId) {
            if (!preferences.bookLanguages || typeof preferences.bookLanguages !== 'object') preferences.bookLanguages = {};
            preferences.bookLanguages[settingsContext.bookId] = languageSelect.value;
        }
        try { localStorage.setItem(preferenceStorageKey, JSON.stringify(preferences)); } catch { /* Optional. */ }
        dialog.close();
        if (errorNotice) errorNotice.hidden = true;
        document.dispatchEvent(new CustomEvent('glosify:speech-settings-changed'));
    });

    window.GlosifyTts = Object.freeze({
        playSavedQueue: playSavedQueue,
        openSettings: openSettings,
        getBookLanguage: getBookLanguage,
        getProvider: getProvider,
        estimateQueue: estimateQueue,
        playQueue: playQueue,
        stop: stop,
    });

    document.addEventListener('click', function (event) {
        var settings = event.target.closest('[data-speech-settings]');
        if (settings) {
            event.preventDefault();
            var readerSettings = document.querySelector('[data-reader-speech-settings]');
            if (readerSettings) { readerSettings.click(); return; }
            var contentButton = document.querySelector('[data-tts]');
            openSettings(lastContext.lang ? lastContext : { lang: contentButton?.getAttribute('data-tts-lang') || '' });
            return;
        }
        var button = event.target.closest('[data-tts]');
        if (!button) return;
        event.preventDefault();

        var text = button.getAttribute('data-tts');
        var lang = button.getAttribute('data-tts-lang') || '';
        if (!text) return;

        var wasPlaying = button.classList.contains('is-playing');
        stop();
        if (wasPlaying) return;

        playSavedQueue([{ text: text, lang: lang }], {
            onStateChange: function (state, error) {
                button.classList.toggle('is-playing', state === 'playing');
                button.setAttribute('aria-pressed', String(state === 'playing'));
                if (state === 'error') console.warn('Text-to-speech failed.', error);
            },
        });
    });
})();
