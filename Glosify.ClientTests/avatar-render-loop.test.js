import test from 'node:test';
import assert from 'node:assert/strict';
import { createPerformance } from './build/avatar/performance.js';
import { createRenderLoop, portraitFraming } from './build/avatar/render-loop.js';

function harness() {
    let time = 0, next = 0;
    const pending = new Map(), draws = [], samples = [];
    const loop = createRenderLoop(dt => draws.push({ time, dt }), {
        now: () => time,
        onFrame: sample => samples.push(sample),
        request: callback => { pending.set(++next, callback); return next; },
        cancel: id => pending.delete(id)
    });
    return { loop, draws, pending, samples, advance(ms) {
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

test('closing a sidebar widens the stage without zooming the portrait out', () => {
    for (const height of [620, 720, 900]) {
        const open = portraitFraming(520, height);
        for (const width of [800, 1120, 1360, 1500]) assert.deepEqual(portraitFraming(width, height), open);
    }
});

test('the phone layout widens the view to keep the shoulders and hair in frame', () => {
    const extent = ({ fov }) => 2 * 1.65 * Math.tan(fov / 2);
    const phone = portraitFraming(366, 540, { compact: true }), desktop = portraitFraming(366, 540);
    assert.ok(phone.fov > desktop.fov);
    assert.ok(extent(phone) * 366 / 540 >= .5 - 1e-9);
    assert.deepEqual(portraitFraming(800, 540, { compact: true }), portraitFraming(800, 540));
    for (const framing of [phone, desktop]) assert.ok(framing.targetY + extent(framing) / 2 > 1.75);
});


test('render timing samples the next animation callback rather than the intentional idle interval', () => {
    const h = harness(); h.advance(3000);
    assert.ok(h.samples.length >= 34);
    assert.ok(h.samples.every(sample => Math.abs(sample.frameGap - 1000 / 60) < .001));
    h.loop.setVisible(false); const count = h.samples.length; h.advance(10000);
    assert.equal(h.samples.length, count);
    h.loop.setVisible(true); h.advance(1000);
    assert.ok(h.samples.slice(count).every(sample => sample.frameGap < 20));
});
