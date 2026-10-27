export interface VoiceCatalog { rate: number; maxTextLength?: number; voices: { value: string; label: string }[]; }
interface ProviderCatalog {
    creditsPerRequest: number; maxTextLength?: number; configured: boolean;
    voices: { locale: string; shortName: string; displayName: string }[];
}

export function createVoiceCatalog({ ensureVoicesReady, sameSpeechLanguage, requireSpeechAccount, message }: {
    ensureVoicesReady: () => Promise<void>;
    sameSpeechLanguage: (first: string, second: string) => boolean | '';
    requireSpeechAccount: () => void;
    message: (key: string) => string;
}) {
    const catalogCache = new Map<string, Promise<VoiceCatalog>>();
    async function voiceCatalog(provider: string, lang: string, fresh: boolean): Promise<VoiceCatalog> {
        if (provider === 'browser') {
            await ensureVoicesReady();
            return { rate: 0, voices: (window.speechSynthesis?.getVoices() || [])
                .filter(function (voice) { return sameSpeechLanguage(voice.lang, lang); })
                .map(function (voice) { return { value: voice.voiceURI, label: voice.name + ' (' + voice.lang + ')' }; }) };
        }
        requireSpeechAccount();
        if (!fresh && catalogCache.has(lang)) return catalogCache.get(lang)!;
        const request = (async function () {
            const response = await fetch('/api/tts/voices?lang=' + encodeURIComponent(lang), { credentials: 'same-origin' });
            if (response.status === 401) document.dispatchEvent(new CustomEvent('glosify:login-required'));
            if (!response.ok) throw new Error(message('elevenlabsUnavailable'));
            const result: ProviderCatalog = await response.json();
            if (!Number.isSafeInteger(result.creditsPerRequest) || result.creditsPerRequest < 1)
                throw new Error(message('elevenlabsUnavailable'));
            const maxTextLength = result.maxTextLength ?? 180;
            if (!Number.isSafeInteger(maxTextLength) || maxTextLength < 1) throw new Error(message('elevenlabsUnavailable'));
            return { rate: result.creditsPerRequest, maxTextLength: maxTextLength, voices: result.configured ? result.voices
                .filter(function (voice) { return sameSpeechLanguage(voice.locale, lang); })
                .map(function (voice) { return { value: voice.shortName, label: voice.displayName + ' (' + voice.locale + ')' }; }) : [] };
        })();
        catalogCache.set(lang, request);
        try { return await request; } catch (error) { if (catalogCache.get(lang) === request) catalogCache.delete(lang); throw error; }
    }
    return voiceCatalog;
}
