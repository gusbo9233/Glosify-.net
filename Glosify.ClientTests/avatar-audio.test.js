import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
import { AvatarAudio } from '../Glosify/wwwroot/js/avatar/audio.js';

test('capture downsamples hardware input to signed 16 kHz PCM with bounded 200 ms frames', () => {
    let Capture;
    const frames = [];
    const context = vm.createContext({ AudioWorkletProcessor: class { port = { postMessage: x => frames.push(x) }; },
        sampleRate: 48000, registerProcessor: (_, type) => { Capture = type; }, Int16Array, Math });
    vm.runInContext(readFileSync(new URL('../Glosify/wwwroot/js/avatar/capture.worklet.js', import.meta.url), 'utf8'), context);
    const capture = new Capture();
    for (let i = 0; i < 150; i++) capture.process([[new Float32Array(128).fill(-.5)]]);
    assert.equal(frames.length, 2);
    assert.equal(frames[0].pcm.byteLength, 6400);
    assert.equal(new Int16Array(frames[0].pcm)[0], -16384);
    assert.equal(frames[0].rms, .5);
});

test('stopping queued playback invalidates completion and stops every source', async () => {
    const audio = new AvatarAudio(); let stopped = 0;
    audio.context = { currentTime: 0 }; audio.endAt = 10;
    audio.sources.add({ stop() { stopped++; } });
    const finished = audio.finished(); audio.stop();
    assert.equal(await finished, false);
    assert.equal(stopped, 1); assert.equal(audio.endAt, 0);
});

test('closing audio releases microphone tracks and the context', async () => {
    const audio = new AvatarAudio(); let stopped = 0, closed = 0;
    audio.stream = { getTracks: () => [{ stop() { stopped++; } }] };
    audio.context = { state: 'running', close: async () => { closed++; } };
    await audio.close(); await audio.close();
    assert.equal(stopped, 1); assert.equal(closed, 1);
    assert.equal(audio.context, null);
});

test('release flushes a partial microphone frame before acknowledging commit', () => {
    let Capture; const messages = [];
    const context = vm.createContext({ AudioWorkletProcessor: class { port = { postMessage: x => messages.push(x) }; },
        sampleRate: 16000, registerProcessor: (_, type) => { Capture = type; }, Int16Array, Math });
    vm.runInContext(readFileSync(new URL('../Glosify/wwwroot/js/avatar/capture.worklet.js', import.meta.url), 'utf8'), context);
    const capture = new Capture(); capture.process([[new Float32Array(128).fill(.25)]]);
    capture.port.onmessage({ data: { type: 'flush' } });
    assert.equal(messages[0].pcm.byteLength, 256);
    assert.equal(messages[1].flushed, true);
    assert.equal(capture.position, 0);
});


test('late microphone permission cannot restart capture after End', async () => {
    let allow, stopped = 0;
    const original = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { mediaDevices: {
        getUserMedia: () => new Promise(resolve => { allow = resolve; })
    } } });
    try {
        const audio = new AvatarAudio(); const opening = audio.open(() => {});
        await audio.close();
        allow({ getTracks: () => [{ stop() { stopped++; } }] });
        assert.equal(await opening, false);
        assert.equal(stopped, 1); assert.equal(audio.stream, null); assert.equal(audio.context, null);
    } finally {
        if (original) Object.defineProperty(globalThis, 'navigator', original);
        else delete globalThis.navigator;
    }
});

for (const stage of ['resume', 'worklet']) for (const action of ['stop', 'close'])
test(`${action} during audio ${stage} cancels capture and releases its resources`, async () => {
    let unblock, reached, stopped = 0, closed = 0;
    const pending = new Promise(resolve => { unblock = resolve; });
    const entered = new Promise(resolve => { reached = resolve; });
    const pause = async point => { if (stage === point) { reached(); await pending; } };
    const context = vm.createContext({
        navigator: { mediaDevices: { async getUserMedia() { return { getTracks: () => [{ stop() { stopped++; } }] }; } } },
        AudioContext: class {
            state = 'running'; audioWorklet = { addModule: () => pause('worklet') };
            resume() { return pause('resume'); }
            async close() { closed++; this.state = 'closed'; }
        },
        AudioWorkletNode: class { constructor() { throw Error('Canceled setup must not create capture'); } }
    });
    vm.runInContext(readFileSync(new URL('../Glosify/wwwroot/js/avatar/audio.js', import.meta.url), 'utf8').replace('export class', 'class'), context);
    const audio = vm.runInContext('new AvatarAudio()', context);
    const opening = audio.open(() => assert.fail('Canceled setup must not emit audio'));
    await entered; await audio[action](); unblock();
    assert.equal(await opening, false);
    assert.equal(stopped, 1); assert.equal(closed, 1);
    assert.equal(audio.stream, null); assert.equal(audio.context, null);
});

test('unresponsive capture worklet times out instead of leaving release pending', async () => {
    const audio = new AvatarAudio(); audio.context = { state: 'running' }; audio.capture = { port: { postMessage() {} } };
    await assert.rejects(audio.flush(), /Microphone flush timed out/);
    assert.equal(audio.flushReady, null);
});
