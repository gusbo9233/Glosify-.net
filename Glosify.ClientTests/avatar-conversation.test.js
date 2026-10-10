import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../Glosify/wwwroot/js/avatar/avatar.js', import.meta.url), 'utf8');
function setup(mode = 'push-to-talk') {
    const elements = new Map(), sent = [], listeners = {};
    const element = id => {
        if (!elements.has(id)) elements.set(id, { value: '', dataset: {}, textContent: '', addEventListener() {}, setAttribute() {} });
        return elements.get(id);
    };
    const context = vm.createContext({
        document: { getElementById: element, querySelector: selector => selector.includes('avatar-mode') ? { value: mode } : { value: 'csrf' },
            querySelectorAll: () => [], addEventListener: (name, action) => { listeners[name] = action; } },
        window: { addEventListener() {} }, WebSocket: { OPEN: 1 }, Intl, console,
        createAvatar: () => new Promise(() => {}), fetch: () => new Promise(() => {}),
        wire: value => sent.push(value),
    });
    vm.runInContext(source.replace(/^import .*;\n/gm, ''), context);
    vm.runInContext("ready = true; socket = { readyState: 1, send: wire }; audio = { stop() {}, flush: async () => {} };", context);
    return { sent, elements, run: code => vm.runInContext(code, context),
        frame: index => { context.pcm = Uint8Array.of(index).buffer; vm.runInContext('frame({ pcm, rms: 0 })', context); },
        binary: () => sent.filter(x => typeof x !== 'string').map(x => new Uint8Array(x)[0]),
        commands: () => sent.filter(x => typeof x === 'string').map(x => JSON.parse(x).type),
    };
}

test('push-to-talk preserves initial audio through a two-second provider setup', async () => {
    const h = setup(); h.run('press()');
    for (let i = 0; i < 10; i++) h.frame(i);
    await h.run("onMessage({ type: 'listening' }, epoch)");
    assert.deepEqual(h.binary(), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
    h.frame(10); await h.run('release()');
    assert.deepEqual(h.binary(), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    assert.equal(h.commands().at(-1), 'commit');
});

test('release before Ready sends the buffered utterance then commits without later microphone audio', async () => {
    const h = setup(); h.run('press()');
    h.frame(1); h.frame(2); await h.run('release()'); h.frame(3);
    assert.deepEqual(h.binary(), []);
    await h.run("onMessage({ type: 'listening' }, epoch)");
    assert.deepEqual(h.binary(), [1, 2]);
    assert.equal(h.commands().at(-1), 'commit');
    assert.equal(h.elements.get('avatar-status').textContent, 'Thinking');
});

test('excessive setup delay stops with an explicit retry instead of dropping the first words', () => {
    const h = setup(); h.run('press()');
    for (let i = 0; i < 26; i++) h.frame(i);
    assert.deepEqual(h.binary(), []);
    assert.equal(h.commands().at(-1), 'interrupt');
    assert.match(h.elements.get('avatar-notice').textContent, /too long to connect/);
});

test('hands-free interruption retains only the most recent three frames', async () => {
    const h = setup('hands-free'); h.run("setState('thinking')");
    for (let i = 0; i < 10; i++) h.frame(i);
    h.run('listen()'); await h.run("onMessage({ type: 'listening' }, epoch)");
    assert.deepEqual(h.binary(), [7, 8, 9]);
});

test('Ready during a pending microphone flush preserves its final frame before commit', async () => {
    const h = setup(); h.run('press()'); h.frame(1);
    h.run('let finishFlush; audio.flush = () => new Promise(resolve => { finishFlush = resolve; });');
    const released = h.run('release()');
    await h.run("onMessage({ type: 'listening' }, epoch)");
    assert.equal(h.commands().includes('commit'), false);
    h.frame(2); h.run('finishFlush()'); await released;
    assert.deepEqual(h.binary(), [1, 2]);
    assert.equal(h.commands().at(-1), 'commit');
});
