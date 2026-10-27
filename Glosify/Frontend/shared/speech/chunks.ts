export function splitSpeechItems<T extends { text?: unknown }>(items: T[], maxTextLength: number | undefined, invalidMessage: string) {
    const limit = Math.min(180, maxTextLength || 180);
    return items.flatMap(function (item) {
        let remaining = String(item.text || '').trim();
        const parts = [];
        while (remaining) {
            let end = remaining.length;
            if (end > limit) {
                end = remaining.lastIndexOf(' ', limit);
                if (end <= 0) end = limit;
                const last = remaining.charCodeAt(end - 1);
                if (last >= 0xD800 && last <= 0xDBFF) end -= 1;
                if (!end) throw new Error(invalidMessage);
            }
            parts.push(Object.assign({}, item, { text: remaining.slice(0, end).trim() }));
            remaining = remaining.slice(end).trim();
        }
        return parts;
    });
}
