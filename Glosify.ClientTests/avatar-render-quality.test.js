import test from 'node:test';
import assert from 'node:assert/strict';
import { createRenderQuality, renderScaling, normalizeQuality } from '../Glosify/wwwroot/js/avatar/render-quality.js';

function machine() {
    const quality = createRenderQuality();
    quality.scaling(1280, 820, 2);
    let time = 0;
    quality.reset(time);
    return { quality, run(ms, cost = 16, cpu = 5) {
        for (let i = 0; i < ms; i += 84) {
            time += 84;
            quality.sample({ time, frameGap: cost, renderMs: cpu });
        }
    }, reset() { quality.reset(time); }, scale: () => quality.scaling(1280, 820, 2) };
}

test('Auto starts at native CSS resolution when the scene widens; Retina density is not inverted', () => {
    const q = createRenderQuality();
    assert.equal(q.scaling(940, 820, 1), 1);
    assert.equal(q.scaling(1280, 820, 1), 1);
    assert.equal(q.scaling(1280, 820, 2), 1);
    assert.equal(createRenderQuality('high').scaling(600, 600, 2), .5);
});

test('all quality settings bound both texture dimensions and total pixels', () => {
    for (const mode of ['auto', 'low', 'high']) {
        for (const [w, h, dpr] of [[7680, 4320, 3], [390, 840, 3], [800, 9000, 2], [1280, 820, 1]]) {
            const scale = createRenderQuality(mode).scaling(w, h, dpr);
            assert.ok(w / scale <= 2560.001 && h / scale <= 2560.001);
            assert.ok(w * h / scale ** 2 <= 3000000.1);
        }
    }
    assert.ok(Number.isFinite(renderScaling(0, 0, NaN)));
});

test('fast computers gain Retina detail only after sustained headroom', () => {
    const h = machine();
    h.run(6000); assert.equal(h.scale(), 1);
    h.run(4000); assert.ok(h.scale() < 1);
    h.run(60000); assert.ok(h.scale() >= .5);
});

test('sustained browser or CPU stalls lower quality; isolated delays do not', () => {
    const h = machine(); h.run(3000); h.run(84, 150); h.run(2000);
    assert.equal(h.scale(), 1);
    h.run(10000, 55); assert.ok(h.scale() > 1);
    const cpu = machine(); cpu.run(10000, 16, 55); assert.ok(cpu.scale() > 1);
});

test('cooldown prevents immediate rebound and neutral timing never oscillates', () => {
    const h = machine(); h.run(10000, 55);
    const low = h.scale(); h.run(8000, 16); assert.equal(h.scale(), low);
    h.run(30000, 30); assert.equal(h.scale(), low);
    h.run(12000, 16); assert.equal(h.scale(), low, 'does not retry a resolution already measured too slow');
    assert.equal(h.quality.background(), 'full', 'restores effects at the sustainable resolution');
});

test('resume and resize discard old samples but preserve the learned quality', () => {
    const h = machine(); h.run(10000, 55); const scale = h.scale();
    h.reset(); h.run(1900, 55); assert.equal(h.scale(), scale);
    h.quality.sample({ time: 9000, frameGap: 5000, renderMs: 10 });
    assert.equal(h.scale(), scale);
});

test('manual modes stay fixed under load and Auto can be re-enabled', () => {
    const q = createRenderQuality('high'); const high = q.scaling(1280, 820, 2);
    for (let time = 0; time < 30000; time += 100) q.sample({ time, frameGap: 100, renderMs: 80 });
    assert.equal(q.scaling(1280, 820, 2), high);
    q.setMode('low', 30000); const low = q.scaling(1280, 820, 2);
    assert.ok(low > high);
    for (let time = 30000; time < 60000; time += 100) q.sample({ time, frameGap: 16, renderMs: 5 });
    assert.equal(q.scaling(1280, 820, 2), low);
    q.setMode('auto', 60000); assert.equal(q.scaling(1280, 820, 2), 1);
    assert.equal(normalizeQuality('bad-value'), 'auto');
});


test('Auto reduces background effects before changing avatar resolution', () => {
    const h = machine();
    assert.equal(h.quality.background(), 'full');
    h.run(5000, 55);
    assert.equal(h.quality.background(), 'reduced');
    assert.equal(h.scale(), 1, 'first downgrade preserves avatar pixels');
    h.run(5000, 55);
    assert.ok(h.scale() > 1, 'persistent overload can still lower avatar resolution');
});

test('a stable workload settles at its sustainable resolution and restores the background', () => {
    const q = createRenderQuality();
    let scale = q.scaling(800, 600, 2);
    const lateDensities = new Set();
    for (let time = 0; time < 120000; time += 100) {
        const cost = 1 / scale > 1 ? 50 : 16;
        q.sample({ time, frameGap: cost, renderMs: cost });
        scale = q.scaling(800, 600, 2);
        if (time > 30000) lateDensities.add(1 / scale);
    }
    assert.deepEqual([...lateDensities], [1]);
    assert.equal(q.background(), 'full');
});

test('a background that is too expensive is not repeatedly restored', () => {
    const q = createRenderQuality(); q.scaling(800, 600, 1);
    for (let time = 0; time < 90000; time += 100) {
        const cost = q.background() === 'full' ? 55 : 16;
        q.sample({ time, frameGap: cost, renderMs: cost });
        if (time > 5000) assert.equal(q.background(), 'reduced');
    }
    assert.equal(q.scaling(800, 600, 1), 1, 'background reduction preserves the face resolution');
});

test('temporary pixel caps do not erase the learned density', () => {
    const h = machine(); h.run(60000);
    const learned = h.scale(); assert.ok(learned < 1);
    h.quality.scaling(4000, 3000, 2);
    assert.equal(h.scale(), learned);
});

test('manual quality overrides control background effects as well as avatar resolution', () => {
    const q = createRenderQuality('low');
    assert.equal(q.background(), 'reduced');
    q.setMode('high', 0); assert.equal(q.background(), 'full');
    q.setMode('auto', 0); assert.equal(q.background(), 'full');
});
