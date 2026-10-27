export interface SpeechPreferences {
    provider?: string;
    acceptedElevenLabsRate?: number;
    bookLanguages?: Record<string, string>;
    [voice: `${string}:${string}`]: string | undefined;
}

export function readSpeechPreferences(storageKey: string): SpeechPreferences {
    try {
        const owned = localStorage.getItem(storageKey);
        const value: unknown = JSON.parse(owned || localStorage.getItem('glosify.speech.preferences') || '{}');
        if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
        const source = value as Record<string, unknown>;
        const preferences: SpeechPreferences = {};
        if (typeof source.provider === 'string') preferences.provider = source.provider === 'azure' ? 'browser' : source.provider;
        // Legacy preferences have no owner and cannot carry billing consent into this account.
        if (owned && typeof source.acceptedElevenLabsRate === 'number') preferences.acceptedElevenLabsRate = source.acceptedElevenLabsRate;
        if (source.bookLanguages && typeof source.bookLanguages === 'object' && !Array.isArray(source.bookLanguages)) {
            preferences.bookLanguages = Object.fromEntries(Object.entries(source.bookLanguages).filter((entry): entry is [string, string] => typeof entry[1] === 'string'));
        }
        for (const [key, voice] of Object.entries(source)) {
            if (key.includes(':') && typeof voice === 'string' && !(source.provider === 'azure' && key.startsWith('azure:')))
                preferences[key as `${string}:${string}`] = voice;
        }
        return preferences;
    } catch {
        return {};
    }
}
