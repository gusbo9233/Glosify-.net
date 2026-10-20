export function createPractice(cards, locale = 'en') {
    let index = 0;
    let correct = 0;
    let answered = false;
    const normalize = value => value.trim().normalize('NFC').toLocaleLowerCase(locale);
    return {
        get current() { return cards[index]; },
        get index() { return index; },
        get correct() { return correct; },
        get done() { return index >= cards.length; },
        answer(value) {
            if (answered || index >= cards.length) return null;
            answered = true;
            const matches = normalize(value) === normalize(cards[index].Target);
            if (matches) correct++;
            return matches;
        },
        rate(known) {
            if (answered || index >= cards.length) return;
            answered = true;
            if (known) correct++;
        },
        next() { if (answered) { index++; answered = false; } },
        restart() { index = 0; correct = 0; answered = false; }
    };
}
