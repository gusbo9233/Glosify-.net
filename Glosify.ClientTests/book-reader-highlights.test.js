import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const reader = readFileSync(new URL('../Glosify/wwwroot/js/book-reader.js', import.meta.url), 'utf8');
const drawing = reader.slice(reader.indexOf('const removeHighlightFragments ='), reader.indexOf('const paintNativeSelection ='));

for (const kind of ['selection', 'speech', 'hover', 'linked']) {
    test(`${kind} highlight covers fractional right and bottom edges`, () => {
        const fragments = [];
        const paint = vm.runInNewContext(`${drawing}; paintHighlightRects;`, {
            pageSurface: { getBoundingClientRect: () => ({ left: 10, top: 20, width: 500, height: 700 }) },
            highlightLayer: { querySelectorAll: () => [], append: fragment => fragments.push(fragment) },
            document: { createElement: () => ({ style: {}, dataset: {} }) },
        });
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
