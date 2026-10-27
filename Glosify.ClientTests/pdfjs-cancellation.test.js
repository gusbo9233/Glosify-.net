import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../Glosify/wwwroot/lib/pdfjs/pdf.min.mjs', import.meta.url), 'utf8');
for (const [name, next] of [
    ['PDFFetchStreamReader', 'PDFFetchStreamRangeReader'],
    ['PDFFetchStreamRangeReader', 'NetworkManager'],
]) {
    // Execute the shipped reader class without starting a fetch or PDF worker.
    const start = source.indexOf(`class ${name}{`);
    const end = source.indexOf(`class ${next}{`, start);
    assert.ok(start >= 0 && end > start);
    const Reader = vm.runInNewContext(`${source.slice(start, end)}; ${name};`);

    for (const state of ['errored', 'open', 'closed', 'not-started']) {
        test(`${name} aborts ${state} responses without an unhandled cancellation rejection`, async () => {
            const object = Object.create(Reader.prototype);
            const failure = new Error('response body interrupted');
            let cancelledWith;
            object._reader = state === 'not-started' ? null : new ReadableStream({
                start(controller) {
                    if (state === 'errored') controller.error(failure);
                    if (state === 'closed') controller.close();
                },
                cancel(reason) { cancelledWith = reason; },
            }).getReader();
            object._abortController = new AbortController();
            const reason = new Error('document destroyed');

            object.cancel(reason);
            await new Promise(resolve => setImmediate(resolve));

            assert.equal(object._abortController.signal.aborted, true);
            if (state === 'open') assert.equal(cancelledWith, reason);
            if (state === 'errored') await assert.rejects(object._reader.read(), error => error === failure);
        });
    }
}
