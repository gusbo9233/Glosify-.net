import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const reader = readFileSync(new URL('../Glosify/wwwroot/js/book-reader.js', import.meta.url), 'utf8');
const section = (start, end) => reader.slice(reader.indexOf(start), reader.indexOf(end, reader.indexOf(start)));

function setup() {
    const listeners = {};
    const button = { contains: target => target === 'tts', setAttribute() {} };
    const context = vm.createContext({
        document: { addEventListener: (name, callback) => { listeners[name] = callback; } },
        window: {
            GlosifyTts: { playQueue: items => { context.spoken = items; } },
            addEventListener: (name, callback) => { listeners[name] = callback; },
        },
        isTextInput: target => target === 'voice',
        readerTtsToggle: button,
        readerVoiceControl: { contains: target => target === 'voice' },
        selectionPopover: { hidden: true },
        readerTtsPlaying: false,
        currentPage: 1,
        currentSegments: [{ index: 0, sourceText: 'The complete page.' }],
        lastReaderSelection: null,
        preserveReaderSelection: false,
        captureReaderSelection: () => context.live,
        sourceSpeechLanguage: () => 'en',
        voiceForSpeechLanguage: () => '',
        chunkTextForSpeech: text => [text],
        translationLanguage: { value: 'sv' },
        setStatus() {},
        clearSpeechHighlights() {},
        closeSelectionPopover() {},
        scheduleSelectionPaint() {},
        live: null,
    });
    vm.runInContext([
        section('const updateReaderTtsPrompt =', 'const updateReaderTtsButton ='),
        section('const rememberReaderSelection =', 'const paintSpeechSegment ='),
        section('const buildReaderSpeechQueue =', 'const renderTranslation ='),
        section("document.addEventListener('selectionchange'", "translationContent?.addEventListener('mouseup'"),
        section("document.addEventListener('pointerdown'", "window.addEventListener('keydown'"),
        section("window.addEventListener('keydown'", "originalPane?.addEventListener('wheel'"),
    ].join('\n'), context);
    return {
        select(kind = 'source') {
            context.live = { pageNumber: 1, kind, text: 'word', indices: [0] };
            listeners.selectionchange();
        },
        collapse() { context.live = null; listeners.selectionchange(); },
        pointer(target) { listeners.pointerdown({ target }); },
        key(target, key) { listeners.keydown({ target, key }); },
        play() { vm.runInContext('startReaderTts()', context); return context.spoken.map(item => item.text).join(' '); },
        prompt: () => button.title,
    };
}

for (const kind of ['source', 'translation']) {
    test(`Deselecting ${kind} text restores full-page speech`, () => {
        const reader = setup();
        reader.select(kind);
        assert.equal(reader.play(), 'word');
        reader.pointer(kind);
        reader.collapse();
        assert.equal(reader.prompt(), 'Read this page aloud');
        reader.pointer('tts');
        assert.equal(reader.play(), 'The complete page.');
    });
}

test('Clearing a selection without a pointer restores full-page speech', () => {
    const reader = setup();
    reader.select();
    reader.collapse();
    assert.equal(reader.play(), 'The complete page.');
});

for (const control of ['tts', 'voice']) {
    test(`${control} keyboard interaction preserves selection until keyboard deselection in the reader`, () => {
        const reader = setup();
        reader.select();
        reader.key(control, control === 'voice' ? 'ArrowDown' : 'Enter');
        reader.collapse();
        assert.equal(reader.play(), 'word');
        reader.key('source', 'Escape');
        reader.collapse();
        assert.equal(reader.prompt(), 'Read this page aloud');
        assert.equal(reader.play(), 'The complete page.');
    });

    test(`${control} focus preserves speech selection until a click in the reader`, () => {
        const reader = setup();
        reader.select();
        reader.pointer(control);
        reader.collapse();
        assert.equal(reader.play(), 'word');
        reader.pointer('source');
        reader.pointer('tts');
        assert.equal(reader.play(), 'The complete page.');
    });
}
