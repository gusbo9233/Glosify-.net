import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const reader = readFileSync(new URL('../Glosify/wwwroot/js/book-reader.js', import.meta.url), 'utf8');
// Execute the production page-segmentation code before the reader's DOM/event and PDF-loading setup.
const boundary = reader.indexOf('const decorateTextLayer =');
assert.ok(boundary > 0);
function segment(items, intl = Intl) {
    const build = vm.runInNewContext(`${reader.slice(0, boundary)}; buildPageSegments;`, {
        document: { querySelector: () => null }, window: {}, Intl: intl,
    });
    return JSON.parse(JSON.stringify(build({ items })));
}
function verifyOffsets(items, segments) {
    const reconstructed = items.map(() => '');
    segments.forEach((entry, index) => {
        assert.equal(entry.index, index);
        assert.ok(entry.sourceText.length > 0 && entry.sourceText.length <= 2000);
        assert.deepEqual(entry.itemIndices, entry.itemParts.map(part => part.itemIndex));
        entry.itemParts.forEach(part => {
            const original = items[part.itemIndex].str;
            assert.ok(part.startOffset >= 0 && part.endOffset <= original.length);
            assert.ok(part.endOffset > part.startOffset);
            reconstructed[part.itemIndex] += original.slice(part.startOffset, part.endOffset);
        });
    });
    assert.deepEqual(reconstructed, items.map(item => item.str));
}

for (const [name, intl] of [['Intl sentence segmentation', Intl], ['fallback segmentation', {}]]) {
    test(`Unpunctuated PDF notes fit translation bounds with ${name}`, () => {
        const line = 'vocabulary '.repeat(12).trim();
        const items = Array.from({ length: 30 }, () => ({ str: line, hasEOL: true }));
        const segments = segment(items, intl);
        assert.ok(segments.length > 1);
        assert.equal(segments.map(entry => entry.sourceText).join(' '), items.map(item => item.str).join(' '));
        verifyOffsets(items, segments);
    });
}

test('A long unbroken PDF item is split without gaps or duplicated characters', () => {
    const items = [{ str: 'x'.repeat(4501) }];
    const segments = segment(items);
    assert.deepEqual(segments.map(entry => entry.sourceText.length), [2000, 2000, 501]);
    verifyOffsets(items, segments);
});

test('Normalization expansion is included in the segment budget', () => {
    const items = [{ str: '\uFDFA'.repeat(200) }];
    const segments = segment(items);
    assert.ok(segments.length > 1);
    assert.equal(segments.map(entry => entry.sourceText).join(''), items[0].str.normalize('NFKC'));
    verifyOffsets(items, segments);
});

test('Splits do not cut UTF-16 surrogate pairs used in PDF offsets', () => {
    const items = [{ str: 'a'.repeat(1999) + '😀' + 'b'.repeat(2100) }];
    const segments = segment(items);
    assert.equal(segments.map(entry => entry.sourceText).join(''), items[0].str);
    for (const entry of segments) assert.equal(entry.sourceText.isWellFormed(), true);
    verifyOffsets(items, segments);
});

test('A segment at the existing limit keeps its identity and exact offsets', () => {
    const items = [{ str: 'a'.repeat(2000) }];
    const segments = segment(items);
    assert.equal(segments.length, 1);
    assert.deepEqual(segments[0].itemParts, [{ itemIndex: 0, startOffset: 0, endOffset: 2000 }]);
    verifyOffsets(items, segments);
});

test('Short sentences retain their existing PDF mappings', () => {
    const items = [{ str: 'First sentence. Second sentence.' }];
    const segments = segment(items);
    assert.deepEqual(segments.map(entry => entry.sourceText), ['First sentence.', 'Second sentence.']);
    assert.deepEqual(segments.map(entry => entry.itemParts[0]), [
        { itemIndex: 0, startOffset: 0, endOffset: 16 },
        { itemIndex: 0, startOffset: 16, endOffset: 32 },
    ]);
    verifyOffsets(items, segments);
});

for (const length of [1998, 1999, 2000]) {
    test(`A split beside a paragraph separator keeps the next paragraph at length ${length}`, () => {
        const items = [{ str: 'a'.repeat(length) + '\n\n' + 'b'.repeat(100) }];
        const segments = segment(items, {});
        assert.deepEqual(segments.map(entry => entry.sourceText), ['a'.repeat(length), 'b'.repeat(100)]);
        assert.deepEqual(segments.map(entry => entry.paragraphIndex), [0, 1]);
        assert.equal(segments[1].itemParts[0].startOffset, length + 2);
        verifyOffsets(items, segments);
    });
}
