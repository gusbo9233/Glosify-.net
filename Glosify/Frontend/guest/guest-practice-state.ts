// Matches PracticeDirection.IsTargetToSource and MVC's case-insensitive query binding,
// so the script and the server-rendered page always agree on the direction.
export function isReversePractice(search: string|URLSearchParams|Record<string,string>|string[][]|undefined) {
    for (const [key, value] of new URLSearchParams(search))
        if (key.toLowerCase() === 'practicedirection') return value.toLowerCase() === 'target-to-source';
    return false;
}

export function createPractice(cards: { Source: string; Target: string }[], locale = 'en') {
    let index = 0;
    let correct = 0;
    let answered = false;
    // Typographic apostrophes (bundled French and Welsh, iOS smart punctuation) match the ASCII one.
    const normalize = (value: string) => value.trim().normalize('NFC').replace(/[‘’ʼ]/g, "'").toLocaleLowerCase(locale);
    return {
        get current() { return cards[index]; },
        get index() { return index; },
        get correct() { return correct; },
        get done() { return index >= cards.length; },
        answer(value: string) {
            if (answered || index >= cards.length) return null;
            answered = true;
            // A repeated prompt (French "bonjour" is hello and good morning) accepts each of its answers.
            const given = normalize(value), prompt = cards[index].Source;
            const matches = cards.some((card) => card.Source === prompt && normalize(card.Target) === given);
            if (matches) correct++;
            return matches;
        },
        rate(known: boolean) {
            if (answered || index >= cards.length) return;
            answered = true;
            if (known) correct++;
        },
        next() { if (answered) { index++; answered = false; } },
        restart() { index = 0; correct = 0; answered = false; }
    };
}
