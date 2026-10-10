import test from 'node:test';
import assert from 'node:assert/strict';
import { createPerformance, speechShape } from '../Glosify/wwwroot/js/avatar/performance.js';

// A 512-point analyser spectrum at 48 kHz (93.75 Hz bins) with band levels in dB.
function spectrum(bands) {
    const values = new Float32Array(256).fill(-110);
    for (const [from, to, db] of bands) for (let i = Math.round(from / 93.75); i < Math.round(to / 93.75); i++) values[i] = db;
    return values;
}
const speech = bands => ({ spectrum: spectrum(bands), sampleRate: 48000, level: .15 });
const vowels = {
    ee: speech([[100, 450, -30], [450, 1900, -60], [1900, 3000, -49], [3000, 11000, -73]]),
    oo: speech([[100, 450, -33], [450, 800, -39], [800, 1300, -50], [1300, 3000, -79], [3000, 11000, -80]]),
    aa: speech([[100, 450, -37], [450, 1300, -38], [1300, 1900, -45], [1900, 3000, -68], [3000, 11000, -78]]),
    ss: speech([[100, 450, -50], [450, 1900, -62], [1900, 4500, -56], [4500, 11000, -64]])
};
function seeded(seed = 7) { return () => (seed = (seed * 16807) % 2147483647) / 2147483647; }
function run(rain, seconds, audio = null, reduced = false) {
    let last;
    for (let t = 0; t < seconds; t += .025) last = rain.update(.025, audio, reduced);
    return last;
}

test('speech spectrum spreads front vowels, rounds back vowels and opens the jaw for open vowels', () => {
    const ee = speechShape(vowels.ee.spectrum, 48000, .15), oo = speechShape(vowels.oo.spectrum, 48000, .15);
    const aa = speechShape(vowels.aa.spectrum, 48000, .15), ss = speechShape(vowels.ss.spectrum, 48000, .15);
    assert.ok(ee.wide > .5 && ee.round === 0, JSON.stringify(ee));
    assert.ok(oo.round > .5 && oo.wide === 0, JSON.stringify(oo));
    assert.ok(aa.jaw > .9 && aa.jaw > ee.jaw && aa.jaw > oo.jaw, JSON.stringify(aa));
    assert.ok(ss.part > .6 && ss.jaw < .3, JSON.stringify(ss));
    const quiet = speechShape(vowels.aa.spectrum, 48000, .001);
    assert.deepEqual(Object.values(quiet).map(value => value < .01), [true, true, true, true, true]);
});

test('the mouth follows speech audio and closes again in silence', () => {
    const rain = createPerformance(seeded()); rain.setState('speaking');
    const speaking = run(rain, .3, vowels.aa).weights;
    assert.ok(speaking.jawOpen > .8, String(speaking.jawOpen));
    const silent = run(rain, .5).weights;
    for (const name of ['jawOpen', 'mouthWide', 'mouthRound', 'lipsPart']) assert.ok(silent[name] < .01, name);
});

test('Rain blinks irregularly and holds eye contact between glances', () => {
    const rain = createPerformance(seeded()); rain.setState('idle');
    let blinks = 0, closed = false, contact = 0, frames = 0;
    for (let t = 0; t < 60; t += .025, frames++) {
        const { weights } = rain.update(.025);
        if (weights.blink > .9 && !closed) blinks++;
        closed = weights.blink > .9;
        if (Math.max(weights.lookLeft, weights.lookRight, weights.lookUp, weights.lookDown) < .15) contact++;
    }
    assert.ok(blinks >= 10 && blinks <= 30, `blinks per minute: ${blinks}`);
    assert.ok(contact / frames > .6, `eye contact ratio: ${contact / frames}`);
});

test('listening acknowledges a pause after the learner speaks with a nod', () => {
    const heard = createPerformance(seeded()), silent = createPerformance(seeded());
    for (const rain of [heard, silent]) { rain.setState('listening'); run(rain, 2); }
    for (let t = 0; t < 1.6; t += .2) { heard.hear(.1); silent.hear(.01); run(heard, .2); run(silent, .2); }
    let nod = 0, still = 0;
    for (let t = 0; t < 1.2; t += .025) {
        nod = Math.max(nod, heard.update(.025).weights.nod); still = Math.max(still, silent.update(.025).weights.nod);
    }
    assert.ok(nod > still + .3, `nod ${nod} without speech ${still}`);
});

test('a question raises the brows after Rain finishes speaking', () => {
    const asks = createPerformance(seeded()), tells = createPerformance(seeded());
    for (const [rain, text] of [[asks, 'What did you do today?'], [tells, 'That sounds lovely.']]) {
        rain.setState('speaking'); rain.setReply(text); run(rain, 1); rain.setState('idle');
    }
    let asked = 0, told = 0;
    for (let t = 0; t < 1.5; t += .025) {
        asked = Math.max(asked, asks.update(.025).weights.browRaiseLeft);
        told = Math.max(told, tells.update(.025).weights.browRaiseLeft);
    }
    assert.ok(asked > told + .2, `${asked} vs ${told}`);
});

test('thinking looks away and listening returns to eye contact', () => {
    const rain = createPerformance(seeded()); rain.setState('thinking');
    const thinking = run(rain, 1).weights;
    assert.ok(thinking.lookUp > .2 && Math.max(thinking.lookLeft, thinking.lookRight) > .2, JSON.stringify(thinking));
    assert.ok(thinking.mouthPress > .2 && thinking.browDown > .1);
    rain.setState('listening');
    const listening = run(rain, 1).weights;
    assert.ok(Math.max(listening.lookUp, listening.lookLeft, listening.lookRight) < .2, JSON.stringify(listening));
});

test('reduced motion keeps the face still apart from speech', () => {
    const rain = createPerformance(seeded()); rain.setState('speaking');
    for (let t = 0; t < 20; t += .025) {
        const { weights, posture } = rain.update(.025, vowels.aa, true);
        for (const name of ['blink', 'nod', 'turn', 'tilt', 'shrug', 'breathe', 'lookLeft', 'lookRight', 'lookUp', 'lookDown']) assert.equal(weights[name], 0, name);
        assert.deepEqual(posture, { lean: 0, sway: 0, yaw: 0, rise: 0 });
    }
    assert.ok(rain.update(.025, vowels.aa, true).weights.jawOpen > .8);
});

test('weights stay finite and bounded through long, irregular frames and every state', () => {
    const rain = createPerformance(seeded(3)), random = seeded(11);
    const states = ['off', 'connecting', 'idle', 'listening', 'thinking', 'speaking', 'error'];
    for (let i = 0; i < 4000; i++) {
        if (i % 120 === 0) rain.setState(states[Math.floor(random() * states.length)]);
        if (i % 7 === 0) rain.hear(random() * .2);
        const audio = random() < .5 ? Object.values(vowels)[Math.floor(random() * 4)] : null;
        const { weights, posture } = rain.update(random() < .02 ? 5 : random() * .06, audio);
        for (const [name, value] of Object.entries({ ...weights, ...posture })) {
            assert.ok(Number.isFinite(value), name);
            assert.ok(Math.abs(value) <= 1.5, `${name} ${value}`);
        }
    }
});
