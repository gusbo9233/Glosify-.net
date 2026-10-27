import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { setImmediate } from 'node:timers/promises';

const source = readFileSync(new URL('../Glosify/wwwroot/js/books-library.js', import.meta.url), 'utf8')
    .replace("await import('/lib/pdfjs/pdf.min.mjs')", 'pdfjsFixture');

for (const renderFails of [false, true]) {
    test(`Rejected PDF cleanup preserves ${renderFails ? 'the render error' : 'the thumbnail'} and drains the queue`, async () => {
        const rendering = Promise.withResolvers();
        const canvases = Array.from({ length: 4 }, (_, index) => ({
            dataset: { pdfUrl: `/Books/${index}/File` },
            parentElement: { clientWidth: 112 },
            classList: new Set(),
            getContext: () => ({}),
        }));
        const loaded = [];
        const destroyed = [];
        let activeDocuments = 0;
        let maximumActiveDocuments = 0;
        const pdfjsFixture = {
            GlobalWorkerOptions: {},
            getDocument({ url }) {
                loaded.push(url);
                activeDocuments++;
                maximumActiveDocuments = Math.max(maximumActiveDocuments, activeDocuments);
                return { promise: Promise.resolve({
                    getPage: async () => ({
                        getViewport: () => ({ width: 112, height: 150 }),
                        render: () => ({ promise: rendering.promise.then(() => {
                            if (renderFails && url === canvases[0].dataset.pdfUrl) throw new Error('PDF page failed');
                        }) }),
                    }),
                    destroy: async () => {
                        destroyed.push(url);
                        activeDocuments--;
                        throw new Error('PDF worker teardown failed');
                    },
                }) };
            },
        };

        await vm.runInNewContext(`(async () => { ${source} })()`, {
            document: { querySelector: () => null, querySelectorAll: () => canvases },
            window: { devicePixelRatio: 1 },
            pdfjsFixture,
        });
        await setImmediate();
        assert.equal(loaded.length, 3, 'The fourth thumbnail must wait for a queue slot');

        rendering.resolve();
        await setImmediate();

        assert.deepEqual(loaded, canvases.map(canvas => canvas.dataset.pdfUrl));
        assert.deepEqual(destroyed, loaded, 'Every loaded PDF must be released');
        assert.equal(activeDocuments, 0);
        assert.equal(maximumActiveDocuments, 3);
        canvases.forEach((canvas, index) => {
            assert.deepEqual([...canvas.classList], [renderFails && index === 0 ? 'is-preview-error' : 'is-rendered']);
        });
        // node:test also fails this test if any discarded promise rejects without a handler.
    });
}
