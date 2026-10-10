import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../Glosify/wwwroot/js/avatar/avatar.js', import.meta.url), 'utf8');
function setup(mode = 'push-to-talk') {
    const elements = new Map(), sent = [], listeners = {};
    const element = id => {
        if (!elements.has(id)) elements.set(id, { value: '', dataset: {}, textContent: '', options: [], classList: { toggle() {} }, add(option) { this.options.push(option); }, replaceChildren() {}, addEventListener() {}, setAttribute() {} });
        return elements.get(id);
    };
    const context = vm.createContext({
        document: { getElementById: element, querySelector: selector => selector.includes('avatar-mode') ? { value: mode } : { value: 'csrf' },
            querySelectorAll: () => [], addEventListener: (name, action) => { listeners[name] = action; } },
        window: { addEventListener() {} }, WebSocket: { OPEN: 1 }, Intl, console, URL,
        location: { href: 'https://localhost/Avatar', protocol: 'https:' },
        Option: class { constructor(text, value) { this.text = text; this.value = value; } },
        createAvatar: () => new Promise(() => {}), fetch: () => new Promise(() => {}),
        wire: value => sent.push(value),
    });
    vm.runInContext(source.replace(/^import .*;\n/gm, ''), context);
    vm.runInContext("characterLoading = false; el('character').disabled = false; ready = true; socket = { readyState: 1, send: wire }; audio = { stop() {}, flush: async () => {} };", context);
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


test('current learning language is a label and starting submits the chosen quiz and default partner without a language override', async () => {
    const h = setup();
    h.run(`
        audio = null; modelReady = true;
        fetch = async () => ({ ok: true, json: async () => ({
            available: true, language: 'pl', languageName: 'Polish', balance: 100,
            quizzes: [{ id: 'polish-quiz', name: 'Polish words' }],
            rates: { recognitionPerMinute: 5, replyPerThousandTokens: .08, speechPerThousandCharacters: 18.2258 }
        }) });
        AvatarAudio = class { async open() {} async close() {} };
        WebSocket = class { constructor(url) { wire({ socketUrl: String(url) }); } };
        api = async (path, body) => { wire({ path, body }); return { sessionId: 'session', connectUrl: '/api/avatar/sessions/session/voice' }; };
    `);
    await h.run('initialize()');
    assert.equal(h.elements.get('avatar-language').textContent, 'Practicing Polish');
    assert.equal(h.elements.get('avatar-language').options.length, 0);
    assert.equal(h.elements.get('avatar-start').disabled, false);
    h.elements.get('avatar-quiz').value = 'polish-quiz';
    await h.run('start()');
    const request = h.sent.find(x => x.path === '/api/avatar/sessions');
    assert.deepEqual(JSON.parse(JSON.stringify(request.body)), { quizId: 'polish-quiz', characterId: 'rain' });
    assert.equal(h.sent.at(-1).socketUrl, 'wss://localhost/api/avatar/sessions/session/voice');
    assert.equal(h.elements.get('avatar-quiz').disabled, true);
});

test('no current learning language keeps Start disabled and explains how to choose one', async () => {
    const h = setup();
    h.run(`
        audio = null; modelReady = true;
        fetch = async () => ({ ok: true, json: async () => ({
            available: true, language: null, languageName: null, balance: 100, quizzes: [],
            rates: { recognitionPerMinute: 5, replyPerThousandTokens: .08, speechPerThousandCharacters: 18.2258 }
        }) });
    `);
    await h.run('initialize()');
    assert.equal(h.elements.get('avatar-start').disabled, true);
    assert.match(h.elements.get('avatar-notice').textContent, /Choose a learning language in the sidebar/);
    assert.equal(h.elements.get('avatar-language').options.length, 0);
});


test('choosing Rowan disposes the previous model and submits the chosen partner', async () => {
    const h = setup();
    h.run(`
        audio = null; config = { available: true, language: 'sv' };
        visual = { dispose() { wire({ disposed: 'rain' }); } };
        createAvatar = async (_, id) => { wire({ model: id }); return { setState() {}, setAnalyser() {}, dispose() {} }; };
        AvatarAudio = class { async open() {} async close() {} };
        WebSocket = class { constructor() {} };
        api = async (path, body) => { wire({ path, body }); return { sessionId: 'session', voiceName: 'Male Swedish', nativeVoice: true, connectUrl: '/voice' }; };
    `);
    h.elements.get('avatar-character').value = 'rowan';
    await h.run('chooseCharacter()');
    assert.equal(h.sent.filter(x => x.disposed === 'rain').length, 1);
    assert.equal(h.sent.filter(x => x.model === 'rowan').length, 1);
    assert.equal(h.elements.get('avatar-name').textContent, 'Rowan');
    assert.match(h.elements.get('avatar-voice').textContent, /male voice/);
    assert.equal(h.elements.get('avatar-start').disabled, false);
    await h.run('start()');
    assert.equal(h.sent.find(x => x.path === '/api/avatar/sessions').body.characterId, 'rowan');
    assert.equal(h.elements.get('avatar-character').disabled, true);
    h.elements.get('avatar-character').value = 'rain';
    await h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-character').value, 'rowan');
    assert.equal(h.sent.filter(x => x.model).length, 1);
});

test('character loading blocks start and overlapping changes, then a failure allows another choice', async () => {
    const h = setup();
    h.run(`
        audio = null; config = { available: true, language: 'sv' };
        let failModel;
        createAvatar = () => new Promise((_, reject) => { failModel = reject; });
        AvatarAudio = class { constructor() { throw new Error('Microphone must stay off while loading'); } };
    `);
    h.elements.get('avatar-character').value = 'rowan';
    const loading = h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-start').disabled, true);
    assert.equal(h.elements.get('avatar-character').disabled, true);
    await h.run('start()');
    h.elements.get('avatar-character').value = 'rain';
    await h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-character').value, 'rowan');
    h.run("failModel(new Error('Failed model download'))"); await loading;
    assert.equal(h.elements.get('avatar-start').disabled, true);
    assert.equal(h.elements.get('avatar-character').disabled, false);
    assert.match(h.elements.get('avatar-render-status').textContent, /Rowan could not load/);
    h.run('createAvatar = async () => ({ setState() {}, dispose() {} })');
    h.elements.get('avatar-character').value = 'rain';
    await h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-name').textContent, 'Rain');
    assert.equal(h.elements.get('avatar-start').disabled, false);
});
