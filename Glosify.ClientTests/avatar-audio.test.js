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
