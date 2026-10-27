export function createSpeechLanguages(serializedLocales: string | undefined) {
    // The server derives this map from the canonical quiz-language catalog and
    // ElevenLabs voice map so browser speech cannot drift onto the OS default voice.
    const LOCALE_MAP: Record<string, string> = {};
    try {
        const configuredLocales: Record<string, string> = JSON.parse(serializedLocales || '{}');
        Object.keys(configuredLocales).forEach(function (key) {
            LOCALE_MAP[key.toLowerCase()] = configuredLocales[key];
        });
    } catch (error) {
        console.warn('TTS locale configuration could not be read.', error);
    }

    function normalizeLocale(lang: string | null | undefined) {
        if (!lang) return '';
        const key = String(lang).trim().toLowerCase();
        if (LOCALE_MAP[key]) return LOCALE_MAP[key];
        // Already looks like BCP-47 (e.g. "pt-BR"): preserve casing convention.
        const match = key.match(/^([a-z]{2,3})[-_]([a-z]{2,4})$/);
        if (match) return match[1] + '-' + match[2].toUpperCase();
        return key;
    }

    function sameSpeechLanguage(first: string, second: string) {
        // A generic browser tag is ambiguous even when the content alias maps zh to Mandarin.
        if (String(first || '').trim().toLowerCase() === 'zh' || String(second || '').trim().toLowerCase() === 'zh') return false;
        const a = normalizeLocale(first).toLowerCase();
        const b = normalizeLocale(second).toLowerCase();
        // Cantonese and Mandarin must not be conflated as generic "zh".
        return a === b || (a && b && a.split('-')[0] !== 'zh' && b.split('-')[0] !== 'zh' && a.split('-')[0] === b.split('-')[0]);
    }

    return { normalizeLocale, sameSpeechLanguage };
}
