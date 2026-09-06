import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const script = readFileSync(new URL('../Glosify/wwwroot/js/quiz-settings.js', import.meta.url), 'utf8');

function settings({ words, sentences = 201, start = 0, end = 100, translations = {} }) {
    const node = (extra = {}) => ({
        dataset: {}, textContent: '', value: '', checked: false, listeners: {},
        addEventListener(event, handler) { this.listeners[event] = handler; },
        replaceChildren(text) { this.textContent = text; },
        ...extra,
    });
    const number = node();
    const label = node({ textContent: 'All' });
    const all = node({ dataset: { wordTotal: String(words), sentenceTotal: String(sentences),
        wordValue: String(words), sentenceValue: String(sentences), maxItems: '1000' },
    closest: () => ({ querySelector: selector => selector === '[data-length-num]' ? number : label }) });
    const wordType = node({ value: 'words', checked: true, dataset: { count: String(words), itemLabel: 'Words' } });
    const sentenceType = node({ value: 'sentences', dataset: { count: String(sentences), itemLabel: 'Sentences' } });
    const min = node({ value: String(start) });
    const max = node({ value: String(end) });
    const startButton = node();
    const elements = new Map([
        ['[data-selected-mode]', node()], ['[data-start-button]', startButton],
        ['input[name="WordCount"][data-length-all]', all],
        ['[data-range-min]', min], ['[data-range-max]', max],
    ]);
    const groups = new Map([
        ['input[name="Mode"]', [node({ value: 'typing' })]],
        ['input[name="PracticeItemType"]', [wordType, sentenceType]],
        ['input[name="WordCount"]', [all]],
    ]);
    const document = {
        querySelectorAll: selector => groups.get(selector) ?? [],
        querySelector: selector => selector === 'input[name="PracticeItemType"]:checked'
            ? wordType.checked ? wordType : sentenceType
            : elements.get(selector) ?? null,
        createTextNode: text => text,
    };
    vm.runInNewContext(script, { document, window: {
        glosifyText: (key, fallback) => translations[key] ?? fallback,
    }, Intl });
    return { all, number, label, startButton, selectSentences() {
        wordType.checked = false;
        sentenceType.checked = true;
        sentenceType.listeners.change();
    } };
}

test('All caps oversized quizzes and shows Maximum instead of promising every item', () => {
    const page = settings({ words: 1500 });
    assert.equal(page.all.value, '1000');
    assert.equal(page.number.textContent, '1000');
    assert.equal(page.label.textContent, 'Maximum');
    page.selectSentences();
    assert.equal(page.all.value, '201');
    assert.equal(page.number.textContent, '201');
    assert.equal(page.label.textContent, 'All');
});

test('All counts the same rounded range endpoints as the server', () => {
    const page = settings({ words: 115, start: 5, end: 10 });
    assert.equal(page.all.value, '7');
    assert.equal(page.number.textContent, '7');
    assert.equal(page.label.textContent, 'All');
});

test('Dynamic content changes retain localized Maximum and All labels', () => {
    const page = settings({ words: 1500, translations: {
        'Settings.Maximum': 'Maximalt', 'Common.All': 'Alla',
    } });
    assert.equal(page.label.textContent, 'Maximalt');
    assert.equal(page.all.value, '1000');
    page.selectSentences();
    assert.equal(page.label.textContent, 'Alla');
    assert.equal(page.all.value, '201');
});

test('A range smaller than the ceiling includes every item and remains labelled All', () => {
    const page = settings({ words: 1500, start: 50, end: 100 });
    assert.equal(page.all.value, '750');
    assert.equal(page.label.textContent, 'All');
    assert.equal(page.startButton.disabled, false);
});

test('Empty content keeps a valid count value but disables starting practice', () => {
    const page = settings({ words: 0 });
    assert.equal(page.all.value, '1');
    assert.equal(page.startButton.disabled, true);
});
