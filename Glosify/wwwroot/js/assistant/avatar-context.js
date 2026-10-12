// Only opaque associations live in this tab; the server owns and validates text.
export function avatarAssociations(owner, storage) {
    const key = owner ? `glosify.assistant.avatar.${owner}` : null;
    let saved;
    try { saved = JSON.parse(key && storage?.getItem(key) || '{}'); } catch { saved = {}; }
    const entries = value => Array.isArray(value) ? value.filter(pair => Array.isArray(pair) && pair.length === 2
        && pair.every(id => typeof id === 'string' && id.length > 0 && id.length <= 100)).slice(-128) : [];
    const contexts = new Map(entries(saved?.contexts));
    const threads = new Map(entries(saved?.threads));
    return { contexts, threads, persist() {
        if (!key) return;
        try { storage?.setItem(key, JSON.stringify({ contexts: [...contexts].slice(-128), threads: [...threads].slice(-128) })); }
        catch { /* Disabled storage still permits this page's conversation. */ }
    }};
}
