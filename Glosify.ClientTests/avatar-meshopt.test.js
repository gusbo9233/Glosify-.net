import test from 'node:test';
import assert from 'node:assert/strict';
import { MeshoptWorkerClient } from './build/avatar/meshopt-worker-client.js';

function harness() {
    const sent = [];
    const worker = { terminated: false, postMessage(message, transfer) {
        sent.push(structuredClone(message, { transfer }));
    }, terminate() { this.terminated = true; } };
    return { worker, sent, client: new MeshoptWorkerClient(() => worker) };
}

test('decoder transfers independent compressed views and correlates out-of-order replies', async () => {
    const { worker, sent, client } = harness();
    const source = new Uint8Array([7, 8, 9, 10]);
    const a = client.decode(source.subarray(1, 3), 2, 4, 'ATTRIBUTES', 'OCTAHEDRAL');
    const b = client.decode(source, 4, 2, 'TRIANGLES');
    assert.deepEqual([...source], [7, 8, 9, 10]);
    assert.deepEqual([...sent[0].source], [8, 9]);
    assert.equal(sent[0].filter, 'OCTAHEDRAL');
    worker.onmessage({ data: { id: sent[1].id, result: new Uint8Array([20]) } });
    worker.onmessage({ data: { id: sent[0].id, result: new Uint8Array([10]) } });
    assert.deepEqual([...await a], [10]); assert.deepEqual([...await b], [20]);
    assert.equal(client.pending.size, 0); client.dispose();
});

test('decoding errors reject the affected load instead of leaving it pending', async () => {
    const { worker, sent, client } = harness();
    const pending = client.decode(new Uint8Array([0]), 1, 4, 'ATTRIBUTES');
    worker.onmessage({ data: { id: sent[0].id, error: 'Invalid geometry' } });
    await assert.rejects(pending, /Invalid geometry/);
    assert.equal(client.pending.size, 0); client.dispose();
});

test('worker failure or disposal rejects pending work and prevents future decoding', async () => {
    for (const action of ['error', 'dispose']) {
        const { worker, client } = harness();
        const pending = client.decode(new Uint8Array([0]), 1, 4, 'ATTRIBUTES');
        if (action === 'error') worker.onerror(); else client.dispose();
        await assert.rejects(pending, /failed to load|cancelled/);
        assert.equal(worker.terminated, true);
        assert.equal(client.pending.size, 0);
        await assert.rejects(client.decode(new Uint8Array([0]), 1, 4, 'ATTRIBUTES'), /closed/);
        client.dispose();
    }
});
