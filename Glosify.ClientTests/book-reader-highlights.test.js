import test from 'node:test';
import assert from 'node:assert/strict';
import { loadBrowserModule } from './browser-module.js';
import vm from 'node:vm';

for (const kind of ['selection', 'speech', 'hover', 'linked']) {
    test(`${kind} highlight covers fractional right and bottom edges`, async () => {
        const fragments = [];
        const context = vm.createContext({
            pageSurface: { getBoundingClientRect: () => ({ left: 10, top: 20, width: 500, height: 700 }) },
            highlightLayer: { querySelectorAll: () => [], append: fragment => fragments.push(fragment) },
            document: { createElement: () => ({ style: {}, dataset: {} }) },
        });
        const { createReaderHighlights } = await loadBrowserModule(new URL('./build/books/highlights.js', import.meta.url), context);
        const { paintHighlightRects: paint } = createReaderHighlights(context.pageSurface, context.highlightLayer);
        paint([{ left: 30.75, top: 60.75, right: 100.75, bottom: 80.75, width: 70, height: 20 }], kind);
        assert.equal(fragments.length, 1);
        const { left, top, width, height } = fragments[0].style;
        assert.ok(parseFloat(left) <= 20.75);
        assert.ok(parseFloat(top) <= 40.75);
        assert.ok(parseFloat(left) + parseFloat(width) >= 90.75, 'highlight must reach the selected text’s right edge');
        assert.ok(parseFloat(top) + parseFloat(height) >= 60.75, 'highlight must reach the selected text’s bottom edge');
        assert.equal(parseFloat(width), 71);
        assert.equal(parseFloat(height), 21);
    });
}
