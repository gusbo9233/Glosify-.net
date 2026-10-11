// Only an opaque reference and preferences live in this tab's session storage.
// Transcript text stays in the bounded, owner-checked server memory.
export function createConversationMemory(owner, storage) {
    const key = owner ? `glosify.avatar.memory.${owner}` : null;
    let fallback = {};
    const valid = value => value && typeof value === 'object'
        && (typeof value.language === 'string' || value.language === null)
        && typeof value.characterId === 'string'
        && (typeof value.quizId === 'string' || value.quizId === null);
    function read() {
        try {
            const saved = key && storage?.getItem(key);
            if (saved) {
                const parsed = JSON.parse(saved);
                if (valid(parsed?.preferences)) fallback = {
                    preferences: parsed.preferences,
                    id: typeof parsed.id === 'string' ? parsed.id : undefined
                };
            }
        } catch { /* Disabled or invalid storage still permits memory on this page. */ }
        return fallback;
    }
    function write(value) {
        fallback = value;
        try { if (key) storage?.setItem(key, JSON.stringify(value)); }
        catch {
            // Tab-local fallback. Remove the older value so read() cannot restore it.
            try { storage?.removeItem(key); } catch { /* Storage is unavailable. */ }
        }
    }
    const matches = (entry, selection) => entry?.language === selection.language
        && entry?.characterId === selection.characterId && entry?.quizId === selection.quizId;
    return {
        preferences: () => read().preferences,
        select(selection) {
            const value = read();
            if (matches(value.preferences, selection)) return;
            write({ preferences: selection });
            return value.id;
        },
        get(selection) { const value = read(); return matches(value.preferences, selection) ? value.id : undefined; },
        save(selection, id) {
            if (typeof id === 'string') write({ preferences: selection, id });
        }
    };
}
