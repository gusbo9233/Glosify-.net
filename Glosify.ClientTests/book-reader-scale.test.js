import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const reader = readFileSync(new URL('../Glosify/wwwroot/js/book-reader.js', import.meta.url), 'utf8');
const renderCode = reader.slice(reader.indexOf('const renderPage ='), reader.indexOf('const goToPage ='));

test('Every page render gives PDF text the viewport scale, independently of display density', async () => {
    const observed = [];
    const surfaceStyle = { setProperty(name, value) { this[name] = value; } };
    const canvas = {
        style: {}, getContext: () => ({}), closest: () => ({}),
        cloneNode() { return this; }, replaceWith() {},
    };
    const page = {
        getViewport: ({ scale }) => ({ scale, width: 600 * scale, height: 800 * scale }),
        getTextContent: async () => ({ items: [] }),
        render: () => ({ promise: Promise.resolve(), cancel() {} }),
    };
    const context = vm.createContext({
        pdf: { numPages: 2, getPage: async () => page }, canvas,
        pageSurface: { style: surfaceStyle },
        textLayerElement: { style: {}, replaceChildren() {}, append() {} },
        document: { createElement: () => ({}) },
        window: { devicePixelRatio: 1 },
        pdfjs: { TextLayer: class {
            constructor({ viewport }) {
                observed.push({ viewportScale: viewport.scale, textScale: Number(surfaceStyle['--scale-factor']) });
            }
            async render() {}
            cancel() {}
        } },
        renderGeneration: 0, currentCanvasRenderTask: null, currentTextLayer: null,
        originalPane: {}, rotation: 0, ZOOM_MIN: 0.25, ZOOM_MAX: 5,
        translationEnabled: false, readerTtsToggle: {}, indicator: {}, prev: {}, next: {}, splitViewToggle: {},
        computeScale: () => context.requestedScale,
        t: (key, fallback) => fallback,
        setStatus: message => { if (message) throw new Error(message); },
        buildPageSegments: () => [{ index: 0, sourceText: 'Serwus!' }],
    });
    for (const name of ['stopReaderTts', 'clearReaderTtsStatus', 'closeSelectionPopover',
        'clearReaderHighlights', 'decorateTextLayer', 'updateToolbar', 'updateReaderTtsPrompt', 'syncAssistantPage']) {
        context[name] = () => {};
    }
    const render = vm.runInContext(`${renderCode}; renderPage;`, context);
    for (const density of [1, 2]) {
        context.window.devicePixelRatio = density;
        for (const scale of [1, 2.5, 0.75]) {
            context.requestedScale = scale;
            await render(1);
            assert.equal(observed.at(-1).textScale, scale, 'text sizing must match the PDF before TextLayer construction');
        }
    }
    assert.equal(observed.length, 6);
});
