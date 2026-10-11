import test from 'node:test';
import assert from 'node:assert/strict';
import { createPerformance } from '../Glosify/wwwroot/js/avatar/performance.js';
import { createRenderLoop, portraitScaling } from '../Glosify/wwwroot/js/avatar/render-loop.js';

function harness() {
    let time = 0, next = 0;
    const pending = new Map(), draws = [];
    const loop = createRenderLoop(dt => draws.push({ time, dt }), {
        now: () => time,
        request: callback => { pending.set(++next, callback); return next; },
        cancel: id => pending.delete(id)
    });
    return { loop, draws, pending, advance(ms) {
        const end = time + ms;
        while (time + 1000 / 60 <= end + .001) {
            time += 1000 / 60;
            const callbacks = [...pending.values()]; pending.clear();
            for (const callback of callbacks) callback(time);
        }
    } };
}

test('visible portraits stay alive at 12 fps before, between and after conversations', () => {
    const h = harness();
    for (const state of ['off', 'idle', 'error', 'speaking', 'off']) {
        h.loop.setState(state); h.draws.length = 0; h.advance(5000);
        const expected = state === 'speaking' ? 120 : 60;
        assert.ok(Math.abs(h.draws.length - expected) <= 1, `${state}: ${h.draws.length}`);
        assert.equal(h.pending.size, 1);
    }
    const actor = createPerformance(() => .5);
    const poses = h.draws.map(draw => actor.update(draw.dt));
    assert.ok(poses.some(pose => pose.weights.blink > .5), 'idle portrait visibly blinks');
    const breathing = poses.map(pose => pose.weights.breathe);
    assert.ok(Math.max(...breathing) - Math.min(...breathing) > .8, 'idle portrait keeps breathing');
    h.loop.invalidate(); const before = h.draws.length; h.advance(100);
    assert.ok(h.draws.length > before);
});

test('active conversation renders at 15 fps and speech at 24 fps on a 60 Hz display', () => {
    const h = harness();
    for (const [state, expected] of [['listening', 75], ['thinking', 75], ['speaking', 120]]) {
        h.loop.setState(state); h.draws.length = 0; h.advance(5000);
        assert.ok(Math.abs(h.draws.length - expected) <= 1, `${state}: ${h.draws.length}`);
    }
});

test('invisible portrait cancels work even during speech and resumes without a large time step', () => {
    const h = harness(); h.loop.setState('speaking'); h.advance(2000);
    h.loop.setVisible(false); const before = h.draws.length;
    assert.equal(h.pending.size, 0);
    h.loop.invalidate(); h.loop.setState('listening'); h.advance(10000);
    assert.equal(h.draws.length, before);
    h.loop.setVisible(true); h.advance(500);
    assert.ok(h.draws.length > before);
    assert.ok(h.draws.slice(before).every(draw => draw.dt > 0 && draw.dt <= .1));
});

test('reduced motion stops non-speaking animation, preserves speech and reacts to preference changes', () => {
    const h = harness(); h.loop.setState('off'); h.loop.setReducedMotion(true); h.advance(2000);
    assert.equal(h.pending.size, 0);
    h.loop.setState('speaking'); h.advance(2000);
    assert.equal(h.pending.size, 1);
    h.loop.setState('idle'); h.advance(2000); assert.equal(h.pending.size, 0);
    h.loop.setReducedMotion(false); h.advance(2000); assert.equal(h.pending.size, 1);
});

test('disposal prevents every later wakeup', () => {
    const h = harness(); h.loop.setState('speaking'); h.advance(1000);
    h.loop.setState('speaking'); h.loop.dispose(); const count = h.draws.length;
    h.loop.invalidate(); h.loop.setVisible(false); h.loop.setVisible(true);
    h.loop.setState('listening'); h.loop.setReducedMotion(true); h.advance(5000);
    assert.equal(h.pending.size, 0); assert.equal(h.draws.length, count);
});

test('portrait buffer is bounded to 1000 pixels on either axis without upscaling small canvases', () => {
    for (const [width, height] of [[1800, 820], [390, 540], [800, 1800], [1000, 720]]) {
        const scale = portraitScaling(width, height);
        assert.ok(width / scale <= 1000 && height / scale <= 1000);
        assert.ok(scale >= 1);
    }
    assert.equal(portraitScaling(390, 540), 1);
    assert.equal(portraitScaling(1000, 720, 2), 2 / 1.5);
    assert.equal(portraitScaling(1000, 720, 3), 2);
});
