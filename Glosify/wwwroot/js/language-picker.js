export const normalizeLanguageSearch = value => String(value || '')
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLocaleLowerCase()
    .trim();

export const languageMatches = (searchTerms, query) =>
    normalizeLanguageSearch(searchTerms).includes(normalizeLanguageSearch(query));

export const formatLanguageCount = (template, count, oneTemplate = template) => {
    const value = String(count);
    const localizedTemplate = String((count === 1 ? oneTemplate : template) || '');
    return localizedTemplate.includes('{0}')
        ? localizedTemplate.replace('{0}', value)
        : value;
};

(() => {
    'use strict';

    if (typeof document === 'undefined') return;
    const picker = document.querySelector('[data-language-picker]');
    if (!picker) return;

    const search = picker.querySelector('[data-language-search]');
    const cards = Array.from(picker.querySelectorAll('[data-language-card]'));
    const count = picker.querySelector('[data-language-result-count]');
    const empty = picker.querySelector('[data-language-empty]');
    const countTemplate = picker.dataset.languageCountTemplate;
    const countOneTemplate = picker.dataset.languageCountOneTemplate;
    if (!search || cards.length === 0) return;

    const indexedCards = cards.map(card => ({
        card,
        searchTerms: normalizeLanguageSearch(card.dataset.languageSearchTerms),
    }));

    const filter = () => {
        const query = normalizeLanguageSearch(search.value);
        let visible = 0;
        for (const item of indexedCards) {
            const matches = !query || languageMatches(item.searchTerms, query);
            item.card.hidden = !matches;
            if (matches) visible += 1;
        }

        if (count) count.textContent = formatLanguageCount(countTemplate, visible, countOneTemplate);
        if (empty) empty.hidden = visible !== 0;
    };

    search.addEventListener('input', filter);

    search.addEventListener('keydown', event => {
        if (event.key === 'ArrowDown') {
            const firstVisible = cards.find(card => !card.hidden);
            if (firstVisible) {
                event.preventDefault();
                firstVisible.focus();
            }
        }
        if (event.key === 'Escape' && search.value) {
            search.value = '';
            filter();
        }
    });

    for (const card of cards) {
        card.addEventListener('keydown', event => {
            const visibleCards = cards.filter(candidate => !candidate.hidden);
            const currentIndex = visibleCards.indexOf(card);
            let nextIndex = null;
            if (event.key === 'ArrowRight' || event.key === 'ArrowDown') nextIndex = currentIndex + 1;
            if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') nextIndex = currentIndex - 1;
            if (event.key === 'Home') nextIndex = 0;
            if (event.key === 'End') nextIndex = visibleCards.length - 1;
            if (nextIndex === null) return;

            event.preventDefault();
            visibleCards[(nextIndex + visibleCards.length) % visibleCards.length]?.focus();
        });
    }
})();
