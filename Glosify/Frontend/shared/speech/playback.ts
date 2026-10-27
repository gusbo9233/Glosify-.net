import { cancellationError, isCancellation, safeCall } from './callbacks.js';

export type SpeechState = 'playing' | 'completed' | 'stopped' | 'error';
export interface SpeechResult { state: Exclude<SpeechState, 'playing'>; error: unknown; }
export interface SpeechInput {
    text?: unknown;
    lang?: unknown;
    provider?: unknown;
    maxCredits?: unknown;
    quality?: unknown;
    voice?: unknown;
    meta?: unknown;
}
export interface SpeechItem {
    text: string;
    lang: string;
    provider: 'elevenlabs' | 'browser';
    maxCredits: number;
    quality: string;
    voice: string;
    meta?: unknown;
}
export interface SpeechCallbacks {
    onStateChange?: (state: SpeechState, error: unknown) => void;
    onItemStart?: (item: SpeechItem, index: number, total: number) => void;
    onItemEnd?: (item: SpeechItem, index: number, total: number) => void;
}
interface SpeechSession {
    id: number;
    items: SpeechItem[];
    callbacks: SpeechCallbacks;
    resolve: (result: SpeechResult) => void;
    cancelled: boolean;
    finished: boolean;
    abortController: AbortController | null;
    cancelCurrent: (() => void) | null;
    audio: HTMLAudioElement | null;
    objectUrl: string | null;
    utterance: SpeechSynthesisUtterance | null;
}
interface PlaybackOptions {
    normalizeLocale: (language: string) => string;
    sameSpeechLanguage: (first: string, second: string) => boolean | '';
    beforeStop: () => void;
}

export function createSpeechPlayback({ normalizeLocale, sameSpeechLanguage, beforeStop }: PlaybackOptions) {
    let currentSession: SpeechSession | null = null;
    let nextSessionId = 0;

    function ensureCurrent(session: SpeechSession) {
        if (session.cancelled || currentSession !== session) {
            throw cancellationError();
        }
    }

    function releaseAudio(session: SpeechSession) {
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

    function releaseBrowserSpeech(session: SpeechSession) {
        session.utterance = null;
        if (window.speechSynthesis) {
            window.speechSynthesis.cancel();
        }
    }

    function releaseCurrentItem(session: SpeechSession) {
        if (session.abortController) {
            session.abortController.abort();
            session.abortController = null;
        }
        if (session.cancelCurrent) {
            const cancel = session.cancelCurrent;
            session.cancelCurrent = null;
            cancel();
        }
        releaseAudio(session);
        releaseBrowserSpeech(session);
    }

    function finalize(session: SpeechSession, state: Exclude<SpeechState, 'playing'>, error?: unknown) {
        if (session.finished) return;
        session.finished = true;
        releaseCurrentItem(session);
        if (currentSession === session) currentSession = null;
        safeCall(session.callbacks.onStateChange, state, error || null);
        session.resolve({ state: state, error: error || null });
    }

    function stop() {
        beforeStop();
        if (!currentSession) return false;
        const session = currentSession;
        session.cancelled = true;
        finalize(session, 'stopped');
        return true;
    }

    function pickVoice(locale: string) {
        if (!window.speechSynthesis) return null;
        const voices = window.speechSynthesis.getVoices();
        if (!voices || !voices.length) return null;
        const lower = locale.toLowerCase();
        const exact = voices.filter(function (voice) {
            return voice.lang && voice.lang.toLowerCase() === lower;
        });
        if (exact.length) {
            return exact.find(function (voice) { return voice.localService; }) || exact[0];
        }
        const sameLanguage = voices.filter(function (voice) {
            return voice.lang && sameSpeechLanguage(voice.lang, locale);
        });
        if (sameLanguage.length) {
            return sameLanguage.find(function (voice) { return voice.localService; }) || sameLanguage[0];
        }
        return null;
    }

    function ensureVoicesReady(session?: SpeechSession) {
        return new Promise<void>(function (resolve) {
            if (!window.speechSynthesis) { resolve(); return; }
            const voices = window.speechSynthesis.getVoices();
            if (voices && voices.length) { resolve(); return; }
            let done = false;
            const finish = function () {
                if (done) return;
                done = true;
                window.speechSynthesis.removeEventListener('voiceschanged', finish);
                resolve();
            };
            window.speechSynthesis.addEventListener('voiceschanged', finish);
            setTimeout(finish, 500);
        }).then(function () { if (session) ensureCurrent(session); });
    }

    async function playBrowser(item: SpeechItem, session: SpeechSession) {
        if (!window.speechSynthesis || typeof window.SpeechSynthesisUtterance !== 'function') {
            throw new Error('Text-to-speech is unavailable in this browser.');
        }
        await ensureVoicesReady(session);
        ensureCurrent(session);

        const locale = normalizeLocale(item.lang);
        if (!locale) throw new Error('Choose the language of the text before playback.');
        const utterance = new SpeechSynthesisUtterance(item.text);
        session.utterance = utterance;
        if (locale) {
            utterance.lang = locale;
            const voice = item.voice
                ? window.speechSynthesis.getVoices().find(function (candidate) {
                    return candidate.voiceURI === item.voice && sameSpeechLanguage(candidate.lang, locale);
                })
                : pickVoice(locale);
            if (!voice) throw new Error('No browser voice is available for this language. Choose ElevenLabs or install a browser voice.');
            utterance.voice = voice;
            utterance.lang = voice.lang;
        }

        await new Promise<void>(function (resolve, reject) {
            let settled = false;
            const finish = function (error?: unknown) {
                if (settled) return;
                settled = true;
                utterance.onend = null;
                utterance.onerror = null;
                if (session.cancelCurrent === cancel) session.cancelCurrent = null;
                session.utterance = null;
                if (error) reject(error); else resolve();
            };
            const cancel = function () { finish(cancellationError()); };
            session.cancelCurrent = cancel;
            utterance.onend = function () { finish(); };
            utterance.onerror = function () {
                finish(new Error('Browser text-to-speech playback failed.'));
            };
            window.speechSynthesis.speak(utterance);
        });
    }

    function requireSpeechAccount() {
        if (document.body.dataset.speechUser) return;
        document.dispatchEvent(new CustomEvent('glosify:login-required'));
        throw new Error('Sign in to use ElevenLabs speech.');
    }

    async function playElevenLabs(item: SpeechItem, session: SpeechSession) {
        requireSpeechAccount();
        const controller = new AbortController();
        session.abortController = controller;
        let response;
        try {
            response = await fetch('/api/tts', {
                method: 'POST',
                credentials: 'same-origin',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': document.querySelector<HTMLInputElement>('[data-speech-token] input')?.value || '',
                },
                body: JSON.stringify({ text: item.text, lang: item.lang, voice: item.voice,
                    quality: item.quality, maxCredits: item.maxCredits }),
                signal: controller.signal,
            });
        } finally {
            if (session.abortController === controller) session.abortController = null;
        }
        ensureCurrent(session);

        if (response.status === 401) document.dispatchEvent(new CustomEvent('glosify:login-required'));
        if (!response.ok) {
            let detail = '';
            try { const problem = await response.json(); detail = problem.detail || problem.error || ''; } catch { /* Non-JSON response. */ }
            throw new Error(detail || 'ElevenLabs speech is unavailable. Try again or choose browser speech.');
        }

        const blob = await response.blob();
        ensureCurrent(session);
        const objectUrl = URL.createObjectURL(blob);
        const audio = new Audio(objectUrl);
        session.audio = audio;
        session.objectUrl = objectUrl;

        try {
            await new Promise<void>(function (resolve, reject) {
                let settled = false;
                const finish = function (error?: unknown) {
                    if (settled) return;
                    settled = true;
                    audio.onended = null;
                    audio.onerror = null;
                    if (session.cancelCurrent === cancel) session.cancelCurrent = null;
                    if (error) reject(error); else resolve();
                };
                const cancel = function () { finish(cancellationError()); };
                session.cancelCurrent = cancel;
                audio.onended = function () { finish(); };
                audio.onerror = function () { finish(new Error('Audio playback failed.')); };
                audio.play().catch(finish);
            });
        } finally {
            releaseAudio(session);
        }
    }

    async function playItem(item: SpeechItem, session: SpeechSession) {
        if (item.provider === 'elevenlabs') {
            await playElevenLabs(item, session);
        } else {
            await playBrowser(item, session);
        }
    }

    async function runQueue(session: SpeechSession) {
        try {
            for (let index = 0; index < session.items.length; index += 1) {
                ensureCurrent(session);
                const item = session.items[index];
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

    function playQueue(items: (SpeechInput | null)[] | null | undefined, callbacks?: SpeechCallbacks): Promise<SpeechResult> {
        stop();
        callbacks = callbacks || {};
        const normalizedItems = (Array.isArray(items) ? items : [])
            .map(function (item): SpeechItem {
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
            const emptyError = new Error('No text is available to read.');
            safeCall(callbacks.onStateChange, 'error', emptyError);
            return Promise.resolve({ state: 'error', error: emptyError });
        }

        let resolveResult!: (result: SpeechResult) => void;
        const result = new Promise<SpeechResult>(function (resolve) { resolveResult = resolve; });
        const session: SpeechSession = {
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

    return { playQueue, stop, ensureVoicesReady, requireSpeechAccount };
}
