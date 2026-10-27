export interface ConversationPreferences {
    language: string | null;
    characterId: string;
    quizId: string | null;
    practiceContext?: string;
}
interface MemoryState {
    preferences?: ConversationPreferences;
    id?: string;
    retained?: { preferences: ConversationPreferences; id: string }[];
}

// Only an opaque reference and preferences live in this tab's session storage.
// Transcript text stays in the bounded, owner-checked server memory.
export function createConversationMemory(owner: string | null | undefined, storage?: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>) {
    const key = owner ? `glosify.avatar.memory.${owner}` : null;
    let fallback: MemoryState = {};
    const valid = (input: unknown): input is ConversationPreferences => {
        if (!input || typeof input !== 'object') return false;
        const value = input as Record<string, unknown>;
        return (typeof value.language === 'string' || value.language === null)
        && typeof value.characterId === 'string'
        && (typeof value.quizId === 'string' || value.quizId === null)
        && (value.practiceContext === undefined || (typeof value.practiceContext === 'string' && value.practiceContext.length <= 1000));
    };
    function read() {
        try {
            const saved = key && storage?.getItem(key);
            if (saved) {
                const parsed = JSON.parse(saved);
                if (valid(parsed?.preferences)) fallback = {
                    preferences: parsed.preferences,
                    id: typeof parsed.id === 'string' ? parsed.id : undefined,
                    retained: Array.isArray(parsed.retained) ? parsed.retained.filter((x: { preferences?: unknown; id?: unknown }) => valid(x?.preferences) && typeof x.id === 'string') : []
                };
            }
        } catch { /* Disabled or invalid storage still permits memory on this page. */ }
        return fallback;
    }
    function write(value: MemoryState) {
        fallback = value;
        try { if (key) storage?.setItem(key, JSON.stringify(value)); }
        catch {
            // Tab-local fallback. Remove the older value so read() cannot restore it.
            try { storage?.removeItem(key!); } catch { /* Storage is unavailable. */ }
        }
    }
    const matches = (entry: ConversationPreferences | undefined, selection: ConversationPreferences) => entry?.language === selection.language
        && entry?.characterId === selection.characterId && entry?.quizId === selection.quizId
        && (entry?.practiceContext || '') === (selection.practiceContext || '');
    return {
        preferences: () => read().preferences,
        select(selection: ConversationPreferences) {
            const value = read();
            if (matches(value.preferences, selection)) return;
            const retained = (value.retained || []).filter(x => !matches(x.preferences, selection));
            if (value.id) retained.push({ preferences: value.preferences!, id: value.id });
            const found = value.retained?.find(x => matches(x.preferences, selection));
            write({ preferences: selection, id: found?.id, retained });
            return value.id;
        },
        get(selection: ConversationPreferences) { const value = read(); return matches(value.preferences, selection) ? value.id : value.retained?.find(x => matches(x.preferences, selection))?.id; },
        save(selection: ConversationPreferences, id: unknown) {
            if (typeof id !== 'string') return;
            const value = read();
            const retained = (value.retained || []).filter(x => !matches(x.preferences, selection));
            if (value.id && !matches(value.preferences, selection)) retained.push({ preferences: value.preferences!, id: value.id });
            write({ preferences: selection, id, ...(retained.length ? { retained } : {}) });
        }
    };
}
