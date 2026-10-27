import test from 'node:test';
import assert from 'node:assert/strict';
import { loadBrowserModule } from './browser-module.js';
import vm from 'node:vm';

async function setup() {
    let live = null, spoken = [], bookLanguage = '';
    const listeners = {};
    const button = { contains: target => target === 'tts', setAttribute() {},
        addEventListener(name, callback) { listeners[name] = callback; } };
    const controls = {
        '[data-reader-tts]': button,
        '[data-reader-speech-settings]': { contains: target => target === 'settings', addEventListener() {} },
        '[data-speech-dialog]': { contains: target => target === 'voice' },
        '[data-translation-language]': { value: 'sv' },
    };
    const context = vm.createContext({
        document: { querySelector: selector => controls[selector] || null, addEventListener() {} },
        window: { GlosifyTts: { getBookLanguage: () => bookLanguage, playSavedQueue: items => { spoken = items; } } },
        setTimeout, clearTimeout,
    });
    const { createReaderSpeech } = await loadBrowserModule(new URL('./build/books/speech.js', import.meta.url), context);
    const speech = createReaderSpeech({
        page: () => ({ documentId: 'book-1', pageNumber: 1, segments: [{ index: 0, sourceText: 'The complete page.' }],
            readingLanguage: 'Polish', detectedLanguage: 'English' }),
        captureReaderSelection: () => live,
        setStatus() {}, paintHighlightRects() {}, removeHighlightFragments() {}, segmentClientRects: () => [], paintNativeSelection() {},
    });
    return {
        select(kind = 'source') {
            live = { pageNumber: 1, kind, text: 'word', indices: [0] };
            speech.rememberReaderSelection();
        },
        collapse() { live = null; speech.rememberReaderSelection(); },
        pointer: target => speech.preserveSelection(target, true),
        key: target => speech.preserveSelection(target),
        play() { listeners.click(); return spoken.map(item => item.text).join(' '); },
        prompt: () => button.title,
        language: () => spoken[0].lang,
        overrideLanguage: lang => { bookLanguage = lang; },
    };
}

for (const kind of ['source', 'translation']) {
    test(`Deselecting ${kind} text restores full-page speech`, async () => {
        const reader = await setup();
        reader.select(kind);
        assert.equal(reader.play(), 'word');
        reader.pointer(kind);
        reader.collapse();
        assert.equal(reader.prompt(), 'Read this page aloud');
        reader.pointer('tts');
        assert.equal(reader.play(), 'The complete page.');
    });
}

test('Clearing a selection without a pointer restores full-page speech', async () => {
    const reader = await setup();
    reader.select();
    reader.collapse();
    assert.equal(reader.play(), 'The complete page.');
});

for (const control of ['tts', 'voice', 'settings']) {
    test(`${control} keyboard interaction preserves selection until keyboard deselection in the reader`, async () => {
        const reader = await setup();
        reader.select();
        reader.key(control, control === 'voice' ? 'ArrowDown' : 'Enter');
        reader.collapse();
        assert.equal(reader.play(), 'word');
        reader.key('source', 'Escape');
        reader.collapse();
        assert.equal(reader.prompt(), 'Read this page aloud');
        assert.equal(reader.play(), 'The complete page.');
    });

    test(`${control} focus preserves speech selection until a click in the reader`, async () => {
        const reader = await setup();
        reader.select();
        reader.pointer(control);
        reader.collapse();
        assert.equal(reader.play(), 'word');
        reader.pointer('source');
        reader.pointer('tts');
        assert.equal(reader.play(), 'The complete page.');
    });
}


test('reader uses detected content language, scopes overrides to source text and keeps translation language', async () => {
    const reader = await setup();
    reader.play();
    assert.equal(reader.language(), 'English');
    reader.overrideLanguage('fr-FR');
    reader.play();
    assert.equal(reader.language(), 'fr-FR');
    reader.select('translation');
    reader.play();
    assert.equal(reader.language(), 'sv');
});
