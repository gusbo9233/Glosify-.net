import { requiredElement } from '../dom.js';
import { errorMessage } from '../errors.js';
import { readSpeechPreferences } from './preferences.js';
import { createVoiceCatalog, type VoiceCatalog } from './catalog.js';
import { splitSpeechItems } from './chunks.js';
import { createSpeechLanguages } from './locales.js';
import { createSpeechPlayback, type SpeechInput, type SpeechCallbacks, type SpeechResult, type SpeechState } from './playback.js';
import { cancellationError, isCancellation, safeCall } from './callbacks.js';

interface SpeechContext { lang?: string; bookId?: string; }

export function initializeSpeech() {

    const { normalizeLocale, sameSpeechLanguage } = createSpeechLanguages(document.body?.dataset.ttsLocales);
    const { playQueue, stop, ensureVoicesReady, requireSpeechAccount } = createSpeechPlayback({
        normalizeLocale, sameSpeechLanguage, beforeStop: cancelPreparation,
    });

    function cancelPreparation() {
        ++preparationRevision;
        if (pendingPreparation) {
            const pending = pendingPreparation;
            pendingPreparation = null;
            safeCall(pending.onStateChange, 'stopped', null);
        }
    }

    // Only explicit settings actions open the dialog. Playback reads committed preferences.
    const dialog = document.querySelector<HTMLDialogElement>('[data-speech-dialog]');
    let choiceRevision = 0;
    let preparationRevision = 0;
    let pendingPreparation: SpeechCallbacks | null = null;
    let settingsContext: SpeechContext = {};
    let lastContext: SpeechContext = {};
    const preferenceStorageKey = 'glosify.speech.preferences.user:' + encodeURIComponent(document.body?.dataset.speechUser || 'anonymous');
    const preferences = readSpeechPreferences(preferenceStorageKey);
    const view = dialog ? {
        dialog,
        providerSelect: requiredElement<HTMLSelectElement>(dialog, '[data-speech-provider]'),
        languageSelect: requiredElement<HTMLSelectElement>(dialog, '[data-speech-language]'),
        voiceSelect: requiredElement<HTMLSelectElement>(dialog, '[data-speech-voice]'),
        choiceStatus: requiredElement(dialog, '[data-speech-status]'),
        saveButton: requiredElement<HTMLButtonElement>(dialog, '[data-speech-save]'),
        priceNotice: requiredElement(dialog, '[data-speech-price]'),
        creditNotice: requiredElement(dialog, '[data-speech-credits]'),
    } : null;
    let quotedCredits = 0;
    const errorNotice = document.querySelector<HTMLElement>('[data-reader-speech-error]') || document.querySelector<HTMLElement>('[data-speech-error]');
    const errorText = errorNotice?.querySelector<HTMLElement>('[data-speech-error-text]');
    const voiceCatalog = createVoiceCatalog({ ensureVoicesReady, sameSpeechLanguage, requireSpeechAccount, message });

    function getProvider() { return document.body.dataset.speechUser && preferences.provider === 'elevenlabs' ? 'elevenlabs' : 'browser'; }
    function getBookLanguage(bookId: string) { return preferences.bookLanguages?.[bookId] || ''; }
    function message(key: string) { return dialog?.dataset[key] || key; }
    function reportError(error: unknown) {
        if (errorText) errorText.textContent = errorMessage(error, 'Speech playback failed.');
        if (errorNotice) errorNotice.hidden = false;
    }
    async function loadVoiceChoices() {
        if (!view) return;
        const { dialog, providerSelect, languageSelect, voiceSelect, saveButton, creditNotice, choiceStatus, priceNotice } = view;
        const revision = ++choiceRevision;
        const provider = providerSelect.value;
        const lang = languageSelect.value;
        voiceSelect.replaceChildren();
        saveButton.disabled = true;
        voiceSelect.disabled = true;
        creditNotice.hidden = provider !== 'elevenlabs';
        choiceStatus.textContent = message('loading');
        quotedCredits = 0;
        priceNotice.textContent = '';
        if (!lang) { choiceStatus.textContent = languageSelect.options[0].textContent; return; }
        try {
            const catalog = await voiceCatalog(provider, lang, true);
            if (revision !== choiceRevision || !dialog.open) return;
            quotedCredits = catalog.rate;
            priceNotice.textContent = message('rate').replace('{0}', String(quotedCredits)).replace('{1}', String(catalog.maxTextLength));
            catalog.voices.forEach(function (voice) { voiceSelect.add(new Option(voice.label, voice.value)); });
            const saved = preferences[`${provider}:${lang}`];
            if (catalog.voices.some(function (voice) { return voice.value === saved; })) voiceSelect.value = saved!;
            voiceSelect.disabled = !catalog.voices.length;
            saveButton.disabled = !catalog.voices.length;
            choiceStatus.textContent = catalog.voices.length ? '' : message(provider === 'elevenlabs' ? 'noElevenLabsVoices' : 'noBrowserVoices');
        } catch (error) { if (revision === choiceRevision) choiceStatus.textContent = errorMessage(error, 'Speech playback failed.'); }
    }

    function openSettings(context?: SpeechContext) {
        if (!view) return;
        const { dialog, languageSelect, providerSelect } = view;
        stop();
        settingsContext = Object.assign({}, context || lastContext);
        const locale = normalizeLocale(settingsContext.lang || '');
        languageSelect.value = Array.from(languageSelect.options).some(function (option) { return option.value === locale; }) ? locale : '';
        providerSelect.value = getProvider();
        if (!dialog.open) dialog.showModal();
        loadVoiceChoices();
    }

    async function estimateQueue(items: SpeechInput[]) {
        items = items.filter(function (item) { return String(item.text || '').trim(); });
        const provider = getProvider();
        const label = message(provider === 'elevenlabs' ? 'elevenlabsLabel' : 'browserLabel');
        if (provider !== 'elevenlabs' || !items.length) return label;
        try {
            let total = 0;
            for (const item of items) {
                const catalog = await voiceCatalog(provider, normalizeLocale(String(item.lang || '')), false);
                total += catalog.rate * splitSpeechItems([item], catalog.maxTextLength, message('elevenlabsUnavailable')).length;
            }
            return label + ' · ' + message('estimate').replace('{0}', String(total));
        } catch { return label; }
    }

    async function playSavedQueue(items: SpeechInput[], callbacks: SpeechCallbacks = {}, context?: SpeechContext): Promise<SpeechResult> {
        stop();
        const revision = preparationRevision;
        callbacks = callbacks || {};
        items = items.filter(function (item) { return String(item.text || '').trim(); });
        lastContext = Object.assign({ lang: String(items[0]?.lang || '') }, context);
        if (errorNotice) errorNotice.hidden = true;
        const provider = getProvider();
        // Mark preparation as active so a second Read click cancels it too.
        pendingPreparation = callbacks;
        safeCall(callbacks.onStateChange, 'playing', null);
        try {
            const catalogs = new Map<string, VoiceCatalog>();
            const prepared = [];
            for (const item of items) {
                const lang = normalizeLocale(String(item.lang || ''));
                if (!catalogs.has(lang)) catalogs.set(lang, await voiceCatalog(provider, lang, true));
                if (revision !== preparationRevision) throw cancellationError();
                const catalog = catalogs.get(lang)!;
                if (provider === 'elevenlabs' && (!Number.isSafeInteger(preferences.acceptedElevenLabsRate)
                    || preferences.acceptedElevenLabsRate! < catalog.rate)) throw new Error(message('reviewRate'));
                const saved = preferences[`${provider}:${lang}`];
                if (saved && !catalog.voices.some(function (voice) { return voice.value === saved; }))
                    throw new Error(message('voiceUnavailable'));
                if (!catalog.voices.length) throw new Error(message(provider === 'elevenlabs' ? 'noElevenLabsVoices' : 'noBrowserVoices'));
                prepared.push(...splitSpeechItems([Object.assign({}, item, { lang: lang, provider: provider,
                    voice: saved || catalog.voices[0].value, quality: '', maxCredits: catalog.rate })], catalog.maxTextLength, message('elevenlabsUnavailable')));
            }
            if (revision !== preparationRevision) throw cancellationError();
            pendingPreparation = null;
            return await playQueue(prepared, Object.assign({}, callbacks, {
                onStateChange: function (state: SpeechState, error: unknown) {
                    if (state === 'error') reportError(error);
                    safeCall(callbacks.onStateChange, state, error);
                },
            }));
        } catch (error) {
            if (revision !== preparationRevision) return { state: 'stopped', error: null };
            pendingPreparation = null;
            const state = isCancellation(error) ? 'stopped' : 'error';
            if (state === 'error') reportError(error);
            safeCall(callbacks.onStateChange, state, state === 'error' ? error : null);
            return { state: state, error: state === 'error' ? error : null };
        }
    }

    if (view) {
        const { dialog, providerSelect, languageSelect, voiceSelect, saveButton } = view;
        providerSelect?.addEventListener('change', loadVoiceChoices);
        languageSelect?.addEventListener('change', loadVoiceChoices);
        window.speechSynthesis?.addEventListener('voiceschanged', function () {
            if (dialog?.open && providerSelect.value === 'browser') loadVoiceChoices();
        });
        dialog?.addEventListener('close', function () { if (!dialog.open) ++choiceRevision; });
        requiredElement<HTMLButtonElement>(dialog, '[data-speech-cancel]').addEventListener('click', function () { dialog.close(); });
        saveButton?.addEventListener('click', function () {
            if (!dialog.open || saveButton.disabled) return;
            preferences.provider = providerSelect.value;
            preferences[`${providerSelect.value}:${languageSelect.value}`] = voiceSelect.value;
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

    }

    const api = Object.freeze({
        playSavedQueue: playSavedQueue,
        openSettings: openSettings,
        getBookLanguage: getBookLanguage,
        getProvider: getProvider,
        estimateQueue: estimateQueue,
        playQueue: playQueue,
        stop: stop,
    });

    document.addEventListener('click', function (event) {
        const target = event.target as Element | null;
        const settings = target?.closest?.('[data-speech-settings]');
        if (settings) {
            event.preventDefault();
            const readerSettings = document.querySelector<HTMLElement>('[data-reader-speech-settings]');
            if (readerSettings) { readerSettings.click(); return; }
            const contentButton = document.querySelector('[data-tts]');
            openSettings(lastContext.lang ? lastContext : { lang: contentButton?.getAttribute('data-tts-lang') || '' });
            return;
        }
        const button = target?.closest?.('[data-tts]');
        if (!button) return;
        event.preventDefault();

        const text = button.getAttribute('data-tts');
        const lang = button.getAttribute('data-tts-lang') || '';
        if (!text) return;

        const wasPlaying = button.classList.contains('is-playing');
        stop();
        if (wasPlaying) return;

        playSavedQueue([{ text: text, lang: lang }], {
            onStateChange: function (state: SpeechState, error: unknown) {
                button.classList.toggle('is-playing', state === 'playing');
                button.setAttribute('aria-pressed', String(state === 'playing'));
                if (state === 'error') console.warn('Text-to-speech failed.', error);
            },
        });
    });
    return api;
}

export type SpeechApi = ReturnType<typeof initializeSpeech>;
